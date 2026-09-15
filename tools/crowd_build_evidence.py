"""Bind a clean rebuild and each non-timed check to source, binaries and process receipts.

Invoke inside Invoke-CrowdWholeTaskLocked.ps1. No scheduler, install, remote call,
process termination or performance matrix is provided here.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / "tools/HlslPerf.CrowdWholeTask/bin/Release/net10.0"
DLL = BIN / "HlslPerf.CrowdWholeTask.dll"


def now(): return datetime.now(timezone.utc).isoformat()
def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""): h.update(block)
    return h.hexdigest()
def read(path): return json.loads(Path(path).read_text(encoding="utf-8-sig"))
def save(path, value): Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
def git(*args): return subprocess.check_output(["git", *args], cwd=ROOT).decode("utf-8").strip()


def source_state():
    status = git("status", "--porcelain", "--untracked-files=all")
    if status: raise ValueError("Clean committed checkout required; preserve and commit intended changes first: " + status)
    names = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode("utf-8").split("\0")
    return {"head": git("rev-parse", "HEAD"), "trackedFiles": {n: sha(ROOT / n) for n in sorted(names) if n}}


def file_manifest(folder, suffixes=None):
    return {p.relative_to(folder).as_posix(): sha(p) for p in sorted(Path(folder).rglob("*"))
            if p.is_file() and (suffixes is None or p.suffix in suffixes)}


def binaries():
    if not DLL.is_file(): raise ValueError("Runner binary is absent")
    return file_manifest(BIN)


def host_identity(dotnet):
    resolved = shutil.which(str(dotnet))
    if not resolved: raise ValueError("Installed dotnet executable not found")
    path = Path(resolved).resolve()
    runtime_paths = []
    for pattern in ["host/fxr/*/hostfxr.dll", "shared/Microsoft.NETCore.App/*/coreclr.dll",
                    "shared/Microsoft.NETCore.App/*/clrjit.dll", "sdk/*/Roslyn/bincore/csc.dll", "sdk/*/MSBuild.dll"]:
        runtime_paths += list(path.parent.glob(pattern))
    return {"path": str(path), "sha256": sha(path),
            "installedRuntimeAndCompiler": {p.relative_to(path.parent).as_posix(): sha(p) for p in sorted(runtime_paths)}}


def require_lock(stage):
    value = os.environ.get("HLSLPERF_CROWD_LOCK_RECEIPT")
    if not value: raise ValueError("Invoke through the campaign lock wrapper")
    path = Path(value).resolve()
    records = read(path)
    if not isinstance(records, list) or len(records) != 2 or any(r["stage"] != stage for r in records):
        raise ValueError("Missing complete stage-specific preflight")
    if os.environ.get("HLSLPERF_CROWD_LOCK_OWNER") != str(records[-1]["ownerPid"]):
        raise ValueError("Preflight owner mismatch")
    return {"path": str(path), "sha256": sha(path), "stage": stage, "ownerPid": records[-1]["ownerPid"]}


def process(args, log):
    started = now()
    with Path(log).open("w", encoding="utf-8") as stream:
        child = subprocess.Popen([str(a) for a in args], cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
        code = child.wait()  # D3D12 fences have their own timeout; no child is killed here.
    return {"args": [str(a) for a in args], "pid": child.pid, "startedUtc": started, "endedUtc": now(),
            "exitCode": code, "log": str(Path(log).resolve()), "logSha256": sha(log)}


def build(args):
    receipt = {"schemaVersion": 2, "kind": "clean-rebuild", "passed": False, "startedUtc": now(), "commands": []}
    target = args.output / "build-receipt.json"
    try:
        receipt["gate"] = require_lock("build")
        receipt["sourceBefore"] = source_state()
        receipt["host"] = host_identity(args.dotnet)
        dotnet = receipt["host"]["path"]
        commands = [[dotnet, "--info"],
                    [dotnet, "restore", "HlslKernelPipeline.slnx", "--configfile",
                     "examples/HlslPerf.PrimitiveApp/NuGet.offline.config", "-p:NuGetAudit=false", "--disable-build-servers"],
                    [dotnet, "build", "HlslKernelPipeline.slnx", "-c", "Release", "--no-restore", "--no-incremental",
                     "--disable-build-servers", "-p:SourceRevisionId=" + receipt["sourceBefore"]["head"],
                     "-p:IncludeSourceRevisionInInformationalVersion=true"]]
        for index, command in enumerate(commands):
            receipt["commands"].append(process(command, args.output / f"build-{index}.log"))
            save(target, receipt)
            if receipt["commands"][-1]["exitCode"]: raise ValueError("Build command failed; retain its log")
        receipt["sourceAfter"] = source_state()
        if receipt["sourceAfter"] != receipt["sourceBefore"]: raise ValueError("Source changed during build")
        if host_identity(dotnet) != receipt["host"]: raise ValueError("dotnet host changed during build")
        receipt["binaries"] = binaries()
        receipt["testBinaries"] = file_manifest(ROOT / "tests/HlslPerf.Core.Tests/bin/Release/net10.0")
        receipt["passed"] = True
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        receipt["endedUtc"] = now()
        save(target, receipt)
    print("Clean rebuild sealed:", receipt["sourceAfter"]["head"], flush=True)


def verify_build(path, dotnet):
    receipt = read(path)
    if receipt.get("schemaVersion") != 2 or receipt.get("kind") != "clean-rebuild" or not receipt.get("passed"):
        raise ValueError("Successful clean rebuild receipt required")
    if len(receipt["commands"]) != 3 or any(c["exitCode"] or sha(c["log"]) != c["logSha256"] for c in receipt["commands"]):
        raise ValueError("Build command/log identity mismatch")
    if receipt["sourceBefore"] != receipt["sourceAfter"] or source_state() != receipt["sourceAfter"]:
        raise ValueError("Source/commit differs from sealed build")
    if binaries() != receipt["binaries"]: raise ValueError("Runner/dependency binary differs from sealed build")
    if host_identity(dotnet) != receipt["host"]: raise ValueError("dotnet host differs from sealed build")
    return receipt


def verify_debug(folder):
    evidence = read(folder / "debug.json")
    if not isinstance(evidence, dict) or evidence.get("schemaVersion") != 2 or not evidence.get("passed") or not evidence.get("complete") or not evidence.get("snapshots"):
        raise ValueError("Complete structured debug evidence required")
    for row in evidence["snapshots"]:
        s = row["snapshot"]
        def safe_filter(f):
            return isinstance(f, dict) and f.get("available") is True and all(not f[k] for k in ["allowedCategories", "allowedSeverities", "allowedIds", "deniedCategories", "deniedIds"]) and all(v in ["Info", "Message"] for v in f["deniedSeverities"])
        if (not s["available"] or not s["cleared"] or s["discardedMessages"] or not s.get("filtersStable") or
            not safe_filter(s.get("storageFilter")) or not safe_filter(s.get("retrievalFilter")) or
            (s["deniedByStorageFilter"] and not s["storageFilter"]["deniedSeverities"]) or
            s["storedMessages"] != s["retrievableMessages"] or s["storedMessages"] != s["storedMessagesAfterRead"] or
            s["retrievableMessages"] != len(s["messages"]) or any(m["severity"] in ["Error", "Corruption"] for m in s["messages"])):
            raise ValueError("Debug evidence contains missing, lost, filtered or error messages")


def verify_result(mode, folder):
    if mode in ["validate", "check-scenes"]:
        filename, expected = ("correctness.json", 432) if mode == "validate" else ("full-scenes.json", 64)
        evidence = read(folder / filename)
        if not evidence["passed"] or not evidence.get("complete") or evidence["count"] != expected or len(evidence["checks"]) != expected:
            raise ValueError("Incomplete correctness output")
        if any(not r["fullPixelsPassed"] or not r["stableListAndBinsPassed"] for r in evidence["checks"]):
            raise ValueError("Incorrect output contract")
        verify_debug(folder)
        if len(read(folder / "debug.json")["snapshots"]) != expected + 1:
            raise ValueError("Missing per-output or final debug drain")
    elif mode == "debug-control":
        evidence = read(folder / "debug-control.json")
        if (not evidence["passed"] or not evidence["controlOnly"] or not evidence["initial"]["passed"] or
            evidence["overflow"]["passed"] or evidence["overflow"]["discardedMessages"] < 1 or
            evidence["error"]["passed"] or evidence["error"]["errorCount"] != 1 or evidence["error"]["discardedMessages"] or
            evidence["corruption"]["passed"] or evidence["corruption"]["errorCount"] != 1 or evidence["corruption"]["discardedMessages"]):
            raise ValueError("Debug rejection control did not detect the expected failures")
    elif mode == "check-cpu":
        evidence = read(folder / "cpu-check.json")
        pc = evidence["cpuMachine"]["processorCount"]
        boundary_workers = len({1, min(2, pc, 3), min(pc, 3)})
        full_workers = len({n for n in [1, 2, 4, 8, 12, min(12, pc)] if n <= min(12, pc)})
        if (not evidence["passed"] or not evidence["complete"] or evidence["performanceEligible"] or
            evidence["boundaryCount"] != 27 * boundary_workers * 4 or evidence["fullSceneCount"] != 8 * full_workers * 2 or
            evidence["count"] != len(evidence["checks"]) or evidence["count"] != evidence["boundaryCount"] + evidence["fullSceneCount"]):
            raise ValueError("Incomplete CPU scene/worker coverage")
        for row in evidence["checks"]:
            if not row["fullPixelsPassed"] or not row["stableVisibleSequencePassed"] or not row["inputUnchanged"]:
                raise ValueError("CPU output/sequence/input contract failed")
            if row["logicalBytes"] > 2 * 1024**3: raise ValueError("CPU storage budget exceeded")
            if row["kind"] == "full-scene" and sha(folder / (row["atlasSha256"] + ".rgba")) != row["atlasSha256"]:
                raise ValueError("CPU capture changed")
    elif mode in ["rehearse", "run"]:
        evidence = read(folder / "result.json")
        contract = evidence.get("stableListAndBinsPassed") if evidence["arm"] != "cpu-frame-parallel" else evidence.get("stableVisibleSequencePassed") and evidence.get("inputUnchanged")
        if (not evidence["passed"] or evidence["performanceEligible"] != (mode == "run") or evidence["fullOutputChecks"] != 12 or
            not contract or len(evidence["samples"]) != 12):
            raise ValueError("Incomplete or incorrectly labelled rehearsal")
        for sample in evidence["samples"]:
            if not sample["fullByteComparisonPassed"] or sha(folder / f"atlas-{sample['request']:02d}.rgba") != sample["actualHash"]:
                raise ValueError("Rehearsal output differs")


def check(args):
    target = args.output / "check-receipt.json"
    receipt = {"schemaVersion": 2, "kind": "bound-check", "mode": args.check, "passed": False, "startedUtc": now()}
    try:
        stage = "performance" if args.check == "run" else "build" if args.check in ["oracle", "cpu-tests", "check-cpu"] or (args.check == "rehearse" and args.arm == "cpu-frame-parallel") else "correctness"
        receipt["gate"] = require_lock(stage)
        build_record = verify_build(args.build_receipt, args.dotnet)
        receipt["buildReceipt"] = {"path": str(args.build_receipt.resolve()), "sha256": sha(args.build_receipt), "head": build_record["sourceAfter"]["head"]}
        receipt["before"] = {"source": source_state(), "binaries": binaries(), "host": host_identity(args.dotnet)}
        if args.references:
            receipt["referencesBefore"] = file_manifest(args.references, {".json", ".rgba"})
        if args.check == "run":
            if not getattr(args, "protocol", None) or getattr(args, "protocol_index", None) is None:
                raise ValueError("Timed run requires a version-2 protocol and explicit schedule index")
            plan = read(args.protocol)
            if plan.get("schemaVersion") != 2 or plan.get("phase") not in ["discovery", "confirmation"]:
                raise ValueError("Version-2 performance protocol required")
            cell = plan["schedule"][args.protocol_index]
            if (cell["index"] != args.protocol_index or cell["arm"] != args.arm or cell["workers"] != args.workers or
                args.case != plan["phase"] + "-" + cell["case"] or plan["buildReceipt"]["sha256"] != sha(args.build_receipt) or
                plan["references"] != receipt["referencesBefore"] or plan["buildHead"] != build_record["sourceAfter"]["head"]):
                raise ValueError("Timed process does not match its fixed protocol cell")
            for gate in plan["gates"].values():
                if sha(gate["path"]) != gate["sha256"]: raise ValueError("Frozen prerequisite changed")
            receipt["protocol"] = {"path": str(args.protocol.resolve()), "sha256": sha(args.protocol), "cell": cell}
        output = args.output / "result"
        if args.check == "cpu-tests":
            current_tests = file_manifest(ROOT / "tests/HlslPerf.Core.Tests/bin/Release/net10.0")
            if current_tests != build_record["testBinaries"]: raise ValueError("Test binary differs from sealed build")
            receipt["before"]["testBinaries"] = current_tests
            command = [str(args.dotnet), "test", "tests/HlslPerf.Core.Tests", "-c", "Release", "--no-build", "--no-restore",
                       "--results-directory", str(output.resolve()), "--logger", "trx;LogFileName=whole-task.trx"]
        else:
            command = [str(args.dotnet), str(DLL), args.check, str(ROOT), str(output.resolve())]
            if args.check in ["check-scenes", "check-cpu", "rehearse", "run"]:
                if not args.references: raise ValueError("Reference directory required")
                command.append(str(args.references.resolve()))
            if args.check in ["rehearse", "run"]:
                if not args.case or not args.arm: raise ValueError("Case and arm required")
                command += [args.case, args.arm]
                if args.workers is not None: command.append(str(args.workers))
        if args.check == "run":
            from run_crowd_v2 import load_gate
            receipt["performancePreflight"] = load_gate()
            if not receipt["performancePreflight"]["passed"]: raise ValueError("Final prelaunch load gate failed; no process launched")
        receipt["process"] = process(command, args.output / "process.log")
        if args.check == "run": receipt["performancePostflight"] = load_gate()
        receipt["after"] = {"source": source_state(), "binaries": binaries(), "host": host_identity(args.dotnet)}
        if args.check == "cpu-tests":
            receipt["after"]["testBinaries"] = file_manifest(ROOT / "tests/HlslPerf.Core.Tests/bin/Release/net10.0")
        if receipt["before"] != receipt["after"]: raise ValueError("Source/binary/host changed during check")
        if sha(args.build_receipt) != receipt["buildReceipt"]["sha256"]: raise ValueError("Build receipt changed during check")
        if args.check == "run":
            if sha(args.protocol) != receipt["protocol"]["sha256"]: raise ValueError("Performance protocol changed during process")
            if not receipt["performancePostflight"]["passed"]: raise ValueError("Post-process load gate failed; retain and invalidate the attempt")
        if args.references and file_manifest(args.references, {".json", ".rgba"}) != receipt["referencesBefore"]:
            raise ValueError("Reference changed during check")
        if receipt["process"]["exitCode"]: raise ValueError("Check process failed; retain process.log")
        if args.check != "cpu-tests":
            identity = read(output / "process-identity.json")
            if (identity["pid"] != receipt["process"]["pid"] or
                identity["assemblySha256"] != build_record["binaries"][DLL.name] or
                not identity["informationalVersion"].endswith("+" + receipt["buildReceipt"]["head"])):
                raise ValueError("Running assembly does not identify the sealed build commit")
            verify_result(args.check, output)
        else:
            import xml.etree.ElementTree as ET
            counters = ET.parse(output / "whole-task.trx").find(".//{*}Counters")
            if counters is None or int(counters.attrib["total"]) < 205 or counters.attrib["passed"] != counters.attrib["total"]:
                raise ValueError("Complete CPU test receipt required")
        receipt["artifacts"] = file_manifest(output)
        receipt["passed"] = True
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        receipt["endedUtc"] = now()
        save(target, receipt)
    print("Bound check passed:", args.check, "at", receipt["buildReceipt"]["head"], flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["build", "check"])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--build-receipt", type=Path)
    parser.add_argument("--check", choices=["cpu-tests", "oracle", "debug-control", "validate", "check-scenes", "check-cpu", "rehearse", "run"])
    parser.add_argument("--references", type=Path)
    parser.add_argument("--case")
    parser.add_argument("--arm", choices=["hierarchical", "fused", "wave-tiled", "rts", "cpu-frame-parallel"])
    parser.add_argument("--workers", type=int)
    parser.add_argument("--protocol", type=Path)
    parser.add_argument("--protocol-index", type=int)
    args = parser.parse_args()
    if args.mode == "check" and (not args.build_receipt or not args.check): parser.error("check requires --build-receipt and --check")
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    {"build": build, "check": check}[args.mode](args)


if __name__ == "__main__": main()
