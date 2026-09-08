"""Compile and inspect scan/compaction DXIL; never create a GPU device or run it.

This entry accepts only compiler/output paths. There is no performance mode,
calibration, dispatch, timing API, or path to an existing benchmark runner.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compile_one(dxc, output, label, source, entry, defines, *, reject=False):
    binary = output / (label + ".dxil")
    ir = output / (label + ".ll")
    command = [str(dxc), "-T", "cs_6_6", "-HV", "2018", "-Ges", "-WX", "-O3", "-E", entry,
               "-Fo", str(binary), "-Fc", str(ir)]
    for name, value in defines.items():
        command += ["-D", f"{name}={value}"]
    command.append(str(ROOT / source))
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    (output / (label + ".diagnostics.txt")).write_text(result.stdout + result.stderr, encoding="utf-8")
    if reject:
        if result.returncode == 0:
            raise AssertionError(f"Unsupported candidate compiled: {label}")
        return {"label": label, "entry": entry, "defines": defines, "expectedRejection": True,
                "source": source, "command": command}
    if result.returncode:
        raise RuntimeError(f"Compile failed: {label}\n{result.stdout}{result.stderr}")
    text = ir.read_text(encoding="utf-8")
    record = {"label": label, "source": source, "entry": entry, "defines": defines,
              "command": command, "dxilSha256": sha256(binary), "irSha256": sha256(ir)}
    if entry in ("SinglePassScanWaveTiled", "FusedCompactWaveTiled"):
        wave = defines.get("HLSLPERF_WAVE_SIZE", 32)
        group = defines.get("HLSLPERF_GROUP_SIZE", 256)
        assert f"NumThreads=({group},1,1)" in text
        assert f"WaveSize={wave}" in text
        assert re.search(r'WaveTiledSpine.*global \[' + str(group // wave) + r' x i32\]', text)
        assert "ThreadTotals" not in text  # Legacy 256-word LDS array must not leak into this entry.
        assert "@dx.op.waveReadLaneAt.i32" in text
        assert "@dx.op.waveReadLaneFirst.i32" in text
        assert "@dx.op.atomicCompareExchange.i32" in text
        assert "call void @dx.op.barrier(i32 80, i32 2)" in text  # Device-memory fence remains.
        assert "call void @dx.op.barrier(i32 80, i32 9)" in text  # Group sync remains.
        assert re.search(r"call .*rawBufferLoad.*i8 15, i32", text)
        if entry == "SinglePassScanWaveTiled":
            assert re.search(r"call .*rawBufferStore.*i8 15, i32", text)
        shared_bytes = 0
        for line in text.splitlines():
            if "addrspace(3) global" in line:
                array = re.search(r"global \[(\d+) x i32\]", line)
                assert array or "global i32" in line
                shared_bytes += 4 * (int(array.group(1)) if array else 1)
        assert shared_bytes == 4 * (group // wave + 5)
        record["declaredDxilSharedBytes"] = shared_bytes
    elif entry == "ResetWaveTiledState":
        assert "atomic" not in text.lower()
        assert "wavePrefix" not in text
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dxc", type=Path, required=True, help="Path to the offline DXC compiler")
    parser.add_argument("--output", type=Path, required=True, help="New output directory for compiler artifacts")
    args = parser.parse_args()
    dxc, output = args.dxc.resolve(), args.output.resolve()
    if not dxc.is_file():
        parser.error("DXC compiler does not exist")
    output.mkdir(parents=True, exist_ok=False)
    base = {"HLSLPERF_SCAN_WAVE_TILED": 1, "HLSLPERF_SCAN_BACKEND": 3, "HLSLPERF_SCAN_OPERATOR": 1,
            "HLSLPERF_VECTOR_WIDTH": 4, "HLSLPERF_WAVE_TILED_MAX_POLLS": 4,
            "HLSLPERF_GROUP_SIZE": 256, "HLSLPERF_WAVE_SIZE": 32,
            "HLSLPERF_ELEMENTS_PER_THREAD": 4, "HLSLPERF_SINGLE_PASS_ITEMS_SCALE": 4}
    records = []
    # Small compile-only boundary coverage, not workload generation or tuning.
    shapes = [(256, 32, 16), (256, 64, 16), (32, 32, 4), (64, 64, 64), (1024, 32, 4), (256, 64, 12)]
    for group, wave, items in shapes:
        defines = base | {"HLSLPERF_GROUP_SIZE": group, "HLSLPERF_WAVE_SIZE": wave,
                          "HLSLPERF_SINGLE_PASS_ITEMS_SCALE": items // 4}
        for source, entry in [("kernels/scan.hlsl", "SinglePassScanWaveTiled"),
                              ("kernels/compaction.hlsl", "FusedCompactWaveTiled")]:
            label = f"{entry}-g{group}-w{wave}-i{items}"
            records.append(compile_one(dxc, output, label, source, entry, defines))
    for source in ["kernels/scan.hlsl", "kernels/compaction.hlsl"]:
        records.append(compile_one(dxc, output, Path(source).stem + "-reset", source, "ResetWaveTiledState", base))
    for entry in ["ResetWaveTiledState", "SinglePassScanWaveTiled"]:
        records.append(compile_one(dxc, output, "unity-wrapper-" + entry,
                                   "kernels/consumer/ScanWaveTiled.compute", entry, {}))
    for name, value in [("HLSLPERF_SCAN_WAVE_TILED", 0), ("HLSLPERF_SCAN_BACKEND", 2),
                        ("HLSLPERF_SCAN_OPERATOR", 2), ("HLSLPERF_VECTOR_WIDTH", 1),
                        ("HLSLPERF_WAVE_SIZE", 0), ("HLSLPERF_GROUP_SIZE", 16),
                        ("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 17), ("HLSLPERF_WAVE_TILED_MAX_POLLS", 0),
                        ("HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS", 1)]:
        records.append(compile_one(dxc, output, "reject-" + name, "kernels/scan.hlsl",
                                   "SinglePassScanWaveTiled", base | {name: value}, reject=True))
    for source, entry in [("kernels/scan.hlsl", "SinglePassScan"), ("kernels/scan.hlsl", "BlockScanPass"),
                          ("kernels/scan.hlsl", "ResetSinglePassState"),
                          ("kernels/compaction.hlsl", "FusedCompactSinglePass")]:
        records.append(compile_one(dxc, output, "legacy-" + entry, source, entry,
                                   base | {"HLSLPERF_SCAN_WAVE_TILED": 0}))
    sources = ["kernels/scan.hlsl", "kernels/compaction.hlsl", "kernels/include/hlslperf/scan_u32.hlsli",
               "kernels/include/hlslperf/scan_wave_tiled_u32.hlsli", "kernels/consumer/ScanWaveTiled.compute",
               "tools/check_wave_tiled_scan.py"]
    receipt = {"schema": "hlslperf.scan-wave-tiled.compile-only.v1", "performanceStatus": "Unmeasured",
               "gpuDispatchExecuted": False, "unityImportExecuted": False, "compilerSha256": sha256(dxc),
               "sources": {name: sha256(ROOT / name) for name in sources}, "compilations": records}
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(f"PASS: {len(records)} compiler contracts and static DXIL checks; no GPU dispatch or timing.")


if __name__ == "__main__":
    main()
