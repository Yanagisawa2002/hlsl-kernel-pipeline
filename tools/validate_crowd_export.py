"""One fail-fast CPU distribution rehearsal; no GPU device, tuning or automatic retries."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import time
from datetime import datetime, timezone


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def verify_export(output):
    receipt = json.loads((output / "receipt.json").read_text())
    if receipt["backend"] != "cpu" or receipt["gpuExecuted"] or receipt["performanceStatus"] != "unmeasured":
        raise ValueError("Caller mislabels CPU execution or performance")
    request = receipt["request"]
    width, height, frames = request["width"], request["height"], request["framesPerAtlas"]
    if len(receipt["atlases"]) != request["atlasCount"]:
        raise ValueError("Incomplete atlas set")
    for index, atlas in enumerate(receipt["atlases"]):
        raw_path, bmp_path = output / atlas["rgbaFile"], output / atlas["previewFile"]
        raw, bmp = raw_path.read_bytes(), bmp_path.read_bytes()
        if sha(raw_path) != atlas["rgbaSha256"] or sha(bmp_path) != atlas["previewSha256"]:
            raise ValueError("Export hashes differ")
        if atlas["firstFrame"] != request["firstFrame"] + index * frames or len(raw) != width * height * frames * 4:
            raise ValueError("Frame interval or raw size differs")
        if bmp[:2] != b"BM" or struct.unpack_from("<I", bmp, 2)[0] != len(bmp):
            raise ValueError("Invalid BMP file header")
        offset = struct.unpack_from("<I", bmp, 10)[0]
        if struct.unpack_from("<IiiHHI", bmp, 14) != (40, width * frames, -height, 1, 32, 0):
            raise ValueError("Invalid top-down BMP format")
        if offset != 54 or len(bmp) != offset + len(raw):
            raise ValueError("Invalid BMP byte layout")
        # Decode the delivered preview independently of the C# writer and compare every pixel.
        for frame in range(frames):
            for y in range(height):
                for x in range(width):
                    src = ((frame * height + y) * width + x) * 4
                    dst = offset + (y * width * frames + frame * width + x) * 4
                    b, g, r, _ = bmp[dst:dst + 4]
                    if bytes((r, g, b, 255)) != raw[src:src + 4]:
                        raise ValueError("Preview pixel or frame order differs from raw output")
    return {"passed": True, "atlases": len(receipt["atlases"]), "fullPreviewPixelsCompared":
            width * height * frames * len(receipt["atlases"]), "gpuExecuted": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="new directory outside the source checkout")
    parser.add_argument("--max-seconds", type=int, default=1800)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    dotnet = str(args.dotnet.resolve())
    if output == root or root in output.parents or args.max_seconds not in range(1, 3601):
        parser.error("Use an output outside the checkout and a 1..3600 second bound")
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    record = {"schemaVersion": 1, "runId": output.name, "startedUtc": utc(), "status": "RUNNING",
              "attempts": 1, "gpuStage": "SKIPPED_UBUNTU_NATIVE_D3D12_UNAVAILABLE", "remoteUsed": False,
              "sourceSha": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
              "sourceTree": subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=root, text=True).strip(),
              "dotnetPath": dotnet, "dotnetSha256": sha(dotnet), "pythonVersion": sys.version,
              "maxSeconds": args.max_seconds, "steps": []}
    env = dict(os.environ, DOTNET_ROOT=str(Path(dotnet).parent), DOTNET_CLI_HOME=str(output / "dotnet-home"),
               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
               MSBUILDDISABLENODEREUSE="1", HLSLPERF_TEST_REPOSITORY_ROOT=str(root))
    env["PATH"] = str(Path(dotnet).parent) + os.pathsep + env.get("PATH", "")

    def save():
        (output / "run.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")

    def run(name, command, cwd=root):
        remaining = args.max_seconds - (time.monotonic() - started)
        if remaining <= 0:
            raise TimeoutError("Frozen validation time budget exhausted")
        log = output / (name + ".log")
        step = {"name": name, "command": list(map(str, command)), "cwd": str(cwd), "startedUtc": utc()}
        record["steps"].append(step)
        print("START " + name, flush=True)
        with log.open("wb") as stream:
            process = subprocess.Popen(step["command"], cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                       stdin=subprocess.DEVNULL)
            step["pid"] = process.pid
            save()
            try:
                step["exitCode"] = process.wait(timeout=remaining)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                else:
                    process.kill()
                process.wait()
                step["exitCode"] = process.returncode
                step["timedOut"] = True
            finally:
                step["endedUtc"] = utc()
        step["log"] = log.name
        step["logSha256"] = sha(log)
        save()
        print(f"END {name}: {step['exitCode']}", flush=True)
        if step["exitCode"] != 0 or step.get("timedOut"):
            raise RuntimeError("Frozen validation stopped at " + name + "; no retry")

    save()
    try:
        if subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True).strip():
            raise RuntimeError("Commit the candidate before the single validation run")
        run("01-sdk", [dotnet, "--info"])
        run("02-checkout", [sys.executable, root / "tools/check_source_checkout.py"])
        run("03-upstream", [sys.executable, root / "tools/verify_external_sources.py"])
        run("04-restore", [dotnet, "restore", "HlslKernelPipeline.slnx", "--configfile",
                           "examples/HlslPerf.PrimitiveApp/NuGet.offline.config", "--disable-parallel", "-p:NuGetAudit=false"])
        run("05-build", [dotnet, "build", "HlslKernelPipeline.slnx", "-c", "Release", "--no-restore",
                         "--nologo", "-m:4", "-nr:false", "-p:UseSharedCompilation=false"])
        run("06-tests", [dotnet, "test", "tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj", "-c", "Release",
                         "--no-build", "--no-restore", "--nologo", "--logger", "trx;LogFileName=cpu-tests.trx",
                         "--results-directory", output / "tests", "-m:4", "-nr:false"])
        run("07-primitive-app", [dotnet, "run", "--project", "examples/HlslPerf.PrimitiveApp", "-c", "Release",
                                 "--no-build", "--no-restore", "--", ".", "--check"])
        run("08-pack", [dotnet, "pack", "src/HlslPerf.Workloads/HlslPerf.Workloads.csproj", "-c", "Release",
                        "--no-build", "--no-restore", "-o", output / "packages", "-m:4", "-nr:false"])
        packages = list((output / "packages").glob("EdwinLiu.HlslPerf.Workloads.*.nupkg"))
        if len(packages) != 1:
            raise ValueError("Expected exactly one local workload package")
        run("09-package-contents", [sys.executable, root / "tools/verify_workload_package.py", packages[0]])
        run("10-publish", [dotnet, "publish", "examples/HlslPerf.CrowdExport", "-c", "Release", "--no-build",
                           "--no-restore", "--self-contained", "false", "-p:UseAppHost=false",
                           "-o", output / "app", "-m:4", "-nr:false"])
        for name in ["LICENSE.md", "NOTICE.md", "README.md", "HlslPerf.CrowdExport.dll",
                     "HlslPerf.GpuDriven.dll", "HlslPerf.Core.dll", "HlslPerf.CrowdExport.runtimeconfig.json",
                     "HlslPerf.CrowdExport.deps.json"]:
            if not (output / "app" / name).is_file():
                raise ValueError("Publish folder missing " + name)
        run("11-demo-input", [sys.executable, root / "examples/HlslPerf.CrowdExport/make_demo_input.py", output / "input"])
        run("12-published-caller", [dotnet, output / "app/HlslPerf.CrowdExport.dll", output / "input/request.json",
                                    output / "export"], cwd=output)
        verification = verify_export(output / "export")
        receipt = json.loads((output / "export/receipt.json").read_text())
        if receipt["requestSha256"] != sha(output / "input/request.json") or receipt["inputSha256"] != sha(output / "input/seeds.u32"):
            raise ValueError("Caller did not bind its supplied file inputs")
        if receipt["applicationSha256"] != sha(output / "app/HlslPerf.CrowdExport.dll") or receipt["rendererSha256"] != sha(output / "app/HlslPerf.GpuDriven.dll"):
            raise ValueError("Caller assembly identity mismatch")
        (output / "export-verification.json").write_text(json.dumps(verification, indent=2) + "\n", encoding="utf-8")
        record["status"] = "PASSED"
    except Exception as error:
        record["status"] = "FAILED"
        record["error"] = f"{type(error).__name__}: {error}"
        print(record["error"], flush=True)
    finally:
        record["endedUtc"] = utc()
        record["elapsedSeconds"] = time.monotonic() - started
        record["files"] = [{"path": path.relative_to(output).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}
                           for path in sorted(output.rglob("*")) if path.is_file() and path.name != "run.json"
                           and "dotnet-home" not in path.parts]
        save()
    return 0 if record["status"] == "PASSED" else 1


if __name__ == "__main__":
    sys.exit(main())
