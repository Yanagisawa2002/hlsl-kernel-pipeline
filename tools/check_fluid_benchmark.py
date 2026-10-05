"""Compile generated C#/HLSL and test pure settings. Does not invoke Unity or a GPU."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def checked(command, log):
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", check=False)
    log.write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Command failed; see " + str(log) + "\n" + result.stdout + result.stderr)
    return result.stdout.strip()


def csharp(csc, output, name, sources, references, *, exe=False):
    binary = output / (name + ".dll")
    args = ["-nologo", "-utf8output", "-nostdlib+", "-unsafe+", "-langversion:9.0",
            "-target:" + ("exe" if exe else "library"), '-out:"' + str(binary) + '"']
    args += ['-r:"' + str(p) + '"' for p in references]
    args += ['"' + str(p) + '"' for p in sources]
    response = output / (name + ".rsp")
    response.write_text("\n".join(args) + "\n", encoding="utf-8")
    checked(["dotnet", str(csc), "-noconfig", "@" + str(response)], output / (name + ".diagnostics.txt"))
    return binary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("project", "unity-editor", "mathematics-source", "csc", "dxc", "output"):
        parser.add_argument("--" + option, type=Path, required=True)
    args = parser.parse_args()
    project, editor, math, csc, dxc, output = [getattr(args, name).resolve() for name in
        ("project", "unity_editor", "mathematics_source", "csc", "dxc", "output")]
    if json.loads((math.parent / "package.json").read_text(encoding="utf-8"))["version"] != "1.2.6":
        raise ValueError("Compile against the pinned Unity.Mathematics 1.2.6 package.")
    core = editor / "Data/Managed/UnityEngine/UnityEngine.CoreModule.dll"
    standard = editor / "Data/NetStandard/ref/2.1.0/netstandard.dll"
    for path in (core, standard, csc, dxc, project / "fluid-benchmark-provenance.json"):
        if not path.is_file():
            raise ValueError("Missing compiler/reference/prepared source: " + str(path))
    output.mkdir(parents=True, exist_ok=False)
    math_dll = csharp(csc, output, "Unity.Mathematics", sorted(math.rglob("*.cs")), [standard, core])
    unity = sorted((editor / "Data/Managed/UnityEngine").glob("*.dll"))
    sources = sorted((project / "Assets").rglob("*.cs"))
    runtime = csharp(csc, output, "FluidBenchmarkScripts", sources, [standard, math_dll, *unity])
    # Exercise only parser and filesystem guards, never a Unity object or entry point.
    settings = csharp(csc, output, "FluidSettingsChecks", [ROOT / "unity/FluidScanBenchmark/Tests/SettingsChecks.cs"],
                      [standard, runtime, *unity], exe=True)
    (output / "FluidSettingsChecks.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
        "tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}}), encoding="utf-8")
    shutil.copyfile(core, output / core.name)
    settings_result = checked(["dotnet", str(settings)], output / "settings-checks.txt")

    shader_paths = [
        "Assets/FluidScanBenchmark/Resources/FluidScanBridge.compute",
        "Assets/FluidScanBenchmark/Resources/FluidWaveTiled.compute",
        "Assets/FluidScanBenchmark/Resources/FluidRts.compute",
        "Assets/Scripts/Helpers/GPU Sort/CountSort/Scan/Resources/ScanTest.compute",
        "Assets/Scripts/Helpers/GPU Sort/CountSort/Resources/CountSort.compute",
        "Assets/Scripts/Helpers/SpatialHash/Offsets/Resources/SpatialOffsets.compute",
        "Assets/Scripts/Simulation/Compute/FluidSim.compute",
        "Assets/FluidScanBenchmark/Resources/FluidCountSortRaw.compute",
        "Assets/FluidScanBenchmark/Resources/FluidScanSweepInit.compute",
    ]
    records = []
    for index, relative in enumerate(shader_paths):
        source = project / relative
        for entry in re.findall(r"^#pragma kernel (\w+)", source.read_text(encoding="utf-8"), re.MULTILINE):
            binary = output / f"shader-{index}-{entry}.dxil"
            # Preserve vendor/upstream bytes. Their existing compiler warnings stay in diagnostics.
            # Our bridge and wave wrapper retain warnings-as-errors.
            warnings_as_errors = index in (0, 1, 7, 8)
            target = "cs_6_0" if index == 1 else "cs_6_6"
            checked([str(dxc), "-T", target, "-HV", "2018", "-Ges", *(["-WX"] if warnings_as_errors else []),
                     "-O3", "-E", entry, "-Fo", str(binary), str(source)],
                    output / f"shader-{index}-{entry}.diagnostics.txt")
            records.append({"source": relative, "entry": entry, "target": target, "dxilSha256": sha(binary), "warningsAsErrors": warnings_as_errors})
    receipt = {
        "schema": "hlslperf.fluid-scan.static-validation.v1", "status": "passed",
        "gpuDispatchExecuted": False, "unityInvoked": False, "unityImportExecuted": False,
        "playerBuildExecuted": False, "performanceStatus": "Unmeasured",
        "csharpSourceCount": len(sources), "csharpSourceSha256": {str(p.relative_to(project)): sha(p) for p in sources},
        "unityReferenceSha256": sha(core), "cscSha256": sha(csc), "dxcSha256": sha(dxc),
        "settingsChecks": settings_result, "shaderEntries": records,
        "preparedProjectProvenanceSha256": sha(project / "fluid-benchmark-provenance.json"),
    }
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in receipt.items() if k not in ("shaderEntries", "csharpSourceSha256")}, indent=2))


if __name__ == "__main__":
    main()
