"""Prepare a pinned Unity fluid benchmark project. Never invoke Unity or a GPU."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "unity/FluidScanBenchmark"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def git(repo, *args):
    return subprocess.check_output(["git", "-c", "core.autocrlf=false", "-c", "core.eol=lf", "-C", str(repo), *args])


def snapshot(repo, commit):
    return git(repo, "archive", "--format=tar", commit)


def archive_files(data):
    files = {}
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        for member in archive:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or ":" in member.name or "\\" in member.name:
                raise ValueError("Unsafe archive path: " + member.name)
            if member.isdir():
                continue
            if not member.isfile():
                raise ValueError("Unsupported archive member: " + member.name)
            files[member.name] = archive.extractfile(member).read()
    return files


def prepare(output, fluid_source, timing_plugin=None):
    output = Path(output).resolve()
    if output.exists():
        raise ValueError("Choose a new output directory; existing projects are never overwritten.")
    lock = json.loads((BUNDLE / "dependencies.json").read_text(encoding="utf-8"))
    fluid = lock["fluid"]
    files = archive_files(snapshot(fluid_source, fluid["commit"]))
    for path, expected in fluid["patchedInputs"].items():
        if path not in files or digest(files[path]) != expected:
            raise ValueError("Pinned upstream input hash mismatch: " + path)
    vendor = BUNDLE / "ThirdParty/GPUPrefixSums"
    for path, expected in lock["gpuPrefixSums"]["files"].items():
        if digest((vendor / path).read_bytes()) != expected:
            raise ValueError("Pinned external source hash mismatch: " + path)

    # Complete all checks before creating a destination. Source working trees are read-only.
    original_hash = digest("".join(path + "\0" + digest(data) + "\n" for path, data in sorted(files.items())).encode())
    overlay = {p.relative_to(BUNDLE / "Overlay").as_posix(): p.read_bytes()
               for p in (BUNDLE / "Overlay").rglob("*") if p.is_file()}
    files.update(overlay)
    resources = "Assets/FluidScanBenchmark/Resources/"
    for path in lock["gpuPrefixSums"]["files"]:
        if path.endswith(".compute"):
            destination = resources + "FluidRts.compute"
        elif path.endswith(".hlsl"):
            destination = resources + PurePosixPath(path).name
        else:
            destination = "Assets/FluidScanBenchmark/ThirdParty/GPUPrefixSums/" + path
        files[destination] = (vendor / path).read_bytes()
        if path.endswith(".compute"):
            # Unity ignores pragmas in ordinary includes. Declare the vendor header's
            # VULKAN variant at the root without changing upstream kernel/host operations.
            files[destination] = b"#pragma multi_compile __ VULKAN\n" + files[destination]
    wrapper = (ROOT / "kernels/consumer/ScanWaveTiled.compute").read_text(encoding="utf-8")
    wrapper = wrapper.replace('#include "../include/hlslperf/scan_wave_tiled_u32.hlsli"',
                              '#include "WaveTiled/scan_wave_tiled_u32.hlsli"')
    wrapper = wrapper.replace("#pragma use_dxc", "#pragma use_dxc\n#pragma require wavebasic")
    # Unity's DXC importer targets SM6.0 and rejects the SM6.6 WaveSize attribute.
    # Retain the actual shared wave32 algorithm, with an explicit native-width GPU gate.
    # The repository's ordinary SM6.6 consumer is unchanged.
    wrapper = wrapper.replace("#pragma multi_compile UNITY_DEVICE_SUPPORTS_WAVE_32", "")
    wrapper = wrapper.replace("#define HLSLPERF_WAVE_ATTRIBUTE [WaveSize(32)]",
                              "#define HLSLPERF_WAVE_ATTRIBUTE\n#define HLSLPERF_WAVE_TILED_GUARD_NATIVE_WAVE 1")
    wrapper = wrapper.replace("// Requires SM6.6 and wave32 support; DXC validation is not Unity import validation.",
                              "// Unity native-wave32 adapter; GPU width probe and full-output gates are required.")
    wrapper += "\n#pragma kernel ProbeNativeWaveSize\n[numthreads(256, 1, 1)]\nvoid ProbeNativeWaveSize(uint thread : SV_GroupIndex)\n{\n    Output0.Store(thread * 4, WaveGetLaneCount());\n}\n"
    files[resources + "FluidWaveTiled.compute"] = wrapper.encode()
    files[resources + "WaveTiled/scan_wave_tiled_u32.hlsli"] = (ROOT / "kernels/include/hlslperf/scan_wave_tiled_u32.hlsli").read_bytes()
    files["Assets/FluidScanBenchmark/Native/FluidGpuTiming.cpp"] = (BUNDLE / "Native/FluidGpuTiming.cpp").read_bytes()
    if timing_plugin is not None:
        plugin = Path(timing_plugin).resolve()
        receipt = plugin.with_name("FluidGpuTimingBuild.json")
        native_build = json.loads(receipt.read_text())
        if native_build["sourceSha256"] != digest((BUNDLE / "Native/FluidGpuTiming.cpp").read_bytes()) or native_build["dllSha256"] != digest(plugin.read_bytes()):
            raise ValueError("Native plugin/source build identity mismatch")
        files["Assets/Plugins/x86_64/FluidGpuTiming.dll"] = plugin.read_bytes()
        files[resources + "FluidGpuTimingBuild.json"] = receipt.read_bytes()
    files["Assets/FluidScanBenchmark/LICENSE-HLSLPERF.md"] = (ROOT / "LICENSE.md").read_bytes()
    files["Assets/FluidScanBenchmark/LICENSE-GPUPREFIXSUMS.md"] = (ROOT / "third_party/gpu-prefix-sums/LICENSE").read_bytes()
    files["FLUID-BENCHMARK-README.md"] = (BUNDLE / "README.md").read_bytes()
    # Build settings only select the author-provided scene. No editor/play startup hook is added.
    scene_meta = files["Assets/Scenes/Fluid ScreenSpace 2.unity.meta"].decode()
    scene_guid = next(line.split(": ", 1)[1] for line in scene_meta.splitlines() if line.startswith("guid: "))
    files["ProjectSettings/EditorBuildSettings.asset"] = (
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1045 &1\nEditorBuildSettings:\n"
        "  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Scenes:\n  - enabled: 1\n"
        "    path: Assets/Scenes/Fluid ScreenSpace 2.unity\n    guid: " + scene_guid + "\n  m_configObjects: {}\n").encode()
    payload_hashes = {p: digest(data) for p, data in sorted(files.items())}
    local_hash = digest("".join(p + "\0" + h + "\n" for p, h in payload_hashes.items()).encode())
    source_head = git(ROOT, "rev-parse", "HEAD").decode().strip()
    dirty = bool(git(ROOT, "status", "--porcelain", "--", "unity/FluidScanBenchmark", "tools/prepare_fluid_benchmark.py",
                     "kernels/consumer/ScanWaveTiled.compute", "kernels/include/hlslperf/scan_wave_tiled_u32.hlsli"))
    provenance = {
        "schema": "hlslperf.fluid-scan.prepared.v1", "performanceStatus": "Unmeasured",
        "gpuDispatchExecuted": False, "unityInvoked": False,
        "hlslBaseCommit": source_head, "hlslWorkingTreeModified": dirty,
        "fluidCommit": fluid["commit"], "fluidSnapshotSha256": original_hash,
        "gpuPrefixSumsCommit": lock["gpuPrefixSums"]["commit"],
        "payloadSha256": local_hash, "payloadFiles": payload_hashes,
        "verification": "Preparation/source identity only. Unity import, Player build, GPU correctness and performance remain unvalidated.",
    }
    identity_data = (json.dumps(provenance, indent=2) + "\n").encode()
    files[resources + "FluidBenchmarkProvenance.json"] = identity_data
    files["fluid-benchmark-provenance.json"] = identity_data
    output.mkdir(parents=True)
    for path, data in files.items():
        target = output / Path(*PurePosixPath(path).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return provenance


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timing-plugin", type=Path, help="Optional explicitly built native GPU timestamp DLL")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--fluid-source", type=Path, help="Local repository containing the pinned upstream commit")
    group.add_argument("--download", action="store_true", help="Download source code only; does not run a benchmark")
    args = parser.parse_args()
    if args.download:
        lock = json.loads((BUNDLE / "dependencies.json").read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory(prefix="fluid-source-") as temporary:
            source = Path(temporary) / "Fluid-Sim"
            subprocess.run(["git", "clone", "--no-checkout", lock["fluid"]["url"], str(source)], check=True)
            receipt = prepare(args.output, source, args.timing_plugin)
    else:
        receipt = prepare(args.output, args.fluid_source.resolve(), args.timing_plugin)
    print(json.dumps({k: v for k, v in receipt.items() if k != "payloadFiles"}, indent=2))


if __name__ == "__main__":
    main()
