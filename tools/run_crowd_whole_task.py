"""Supervised whole-task process matrix. Invoke inside the campaign PowerShell lock.

No background service, scheduler, cache clearing, settings changes or process killing.
Every invocation requires a fresh evidence directory. Failed attempts are retained.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import itertools
import json
import math
from pathlib import Path
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
ARMS = ["hierarchical", "fused", "wave-tiled", "rts"]
CASES = ["small", "medium-tail", "large", "dense-tail"]
DLL = ROOT / "tools/HlslPerf.CrowdWholeTask/bin/Release/net10.0/HlslPerf.CrowdWholeTask.dll"


def now(): return datetime.now(timezone.utc).isoformat()
def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""): h.update(block)
    return h.hexdigest()
def read(path): return json.loads(Path(path).read_text(encoding="utf-8-sig"))
def save(path, data): Path(path).write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def snapshot(args):
    save(args.output / "identity.json", identity())
    save(args.output / "git.json", {"utc": now(), "head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                                  "status": subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True)})
    (args.output / "tracked.patch").write_bytes(subprocess.check_output(["git", "diff", "--binary", "HEAD"], cwd=ROOT))


def identity():
    paths = []
    for folder in ["src", "gpu-driven-demo", "kernels", "third_party/gpu-prefix-sums", "tools/HlslPerf.CrowdWholeTask"]:
        paths += [p for p in (ROOT / folder).rglob("*") if p.is_file() and
                  not {"bin", "obj"}.intersection(p.relative_to(ROOT).parts) and
                  p.suffix in {".cs", ".csproj", ".hlsl", ".hlsli", ".h"}]
    paths += [ROOT / p for p in ["Directory.Build.props", "global.json", "third_party/upstream-lock.json",
                                "tools/run_crowd_whole_task.py", "tools/Invoke-CrowdWholeTaskLocked.ps1"]]
    binary = [p for p in DLL.parent.rglob("*") if p.is_file() and p.suffix in {".dll", ".exe", ".json"}]
    return {"source": {p.relative_to(ROOT).as_posix(): sha(p) for p in sorted(set(paths))},
            "binary": {p.relative_to(DLL.parent).as_posix(): sha(p) for p in sorted(binary)}}


def cpu_times():
    idle, kernel, user = ctypes.c_ulonglong(), ctypes.c_ulonglong(), ctypes.c_ulonglong()
    if not ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
        raise OSError("GetSystemTimes failed")
    return idle.value, kernel.value + user.value


def preflight():
    a = cpu_times(); time.sleep(.5); b = cpu_times()
    total = b[1] - a[1]
    cpu = 100 * (1 - (b[0] - a[0]) / total) if total else 100
    p = subprocess.run(["nvidia-smi", "--query-gpu=name,driver_version,utilization.gpu,memory.free,temperature.gpu",
                        "--format=csv,noheader,nounits"], text=True, capture_output=True, check=True)
    gpu = [x.strip() for x in p.stdout.strip().split(",")]
    import shutil
    disk = shutil.disk_usage(ROOT).free
    record = {"utc": now(), "cpuPercent": cpu, "gpu": gpu, "diskFreeBytes": disk}
    record["passed"] = cpu <= 25 and float(gpu[2]) <= 15 and float(gpu[3]) >= 8192 and disk >= 30 * 1024**3
    return record


def launch(dotnet, refs, case, arm, output):
    gate = preflight()
    output.parent.mkdir(parents=True, exist_ok=True)
    save(output.parent / (output.name + "-preflight.json"), gate)
    if not gate["passed"]: raise RuntimeError("Preflight failed; no process launched: " + str(gate))
    args = [str(dotnet), str(DLL), "run", str(ROOT), str(output), str(refs), case, arm]
    start = now()
    with (output.parent / (output.name + ".log")).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, cwd=ROOT)
        # No timeout kill: the D3D12 fence has its own 30-second timeout. A hung
        # process remains owned and is reported for supervised handling.
        result = process.wait()
    receipt = {"args": args, "pid": process.pid, "startedUtc": start, "endedUtc": now(), "exitCode": result,
               "logSha256": sha(output.parent / (output.name + ".log"))}
    save(output.parent / (output.name + "-process.json"), receipt)
    if result: raise RuntimeError("Process failed; retain its log: " + str(output))
    evidence = read(output / "result.json")
    if not evidence["passed"] or not evidence["performanceEligible"] or evidence["fullOutputChecks"] != 12 or not evidence["stableListAndBinsPassed"]:
        raise ValueError("Incomplete process correctness")
    receipt["resultSha256"] = sha(output / "result.json")
    receipt["samplesSha256"] = sha(output / "samples.json")
    receipt["compilationSha256"] = sha(output / "compilation.json")
    save(output.parent / (output.name + "-process.json"), receipt)
    print(case, arm, process.pid, "passed", flush=True)
    return receipt


def discover(args):
    save(args.output / "identity.json", identity())
    records = []
    for case in CASES:
        for arm in ARMS:
            target = args.output / (case + "-" + arm)
            records.append(launch(args.dotnet, args.references, "discovery-" + case, arm, target))
            save(args.output / "processes.json", records)


def freeze(args):
    current = identity()
    if current != read(args.discovery / "identity.json"): raise ValueError("Sources/binaries changed after discovery")
    correct = read(args.validation / "correctness.json")
    if not correct["passed"] or correct["count"] != 432: raise ValueError("Complete correctness gate is required")
    if any(x.startswith(("Error:", "Corruption:")) for x in read(args.validation / "debug.json")):
        raise ValueError("Debug-layer correctness errors")
    full = read(args.full_scenes / "full-scenes.json")
    if not full["passed"] or full["count"] != 64 or full["device"] != correct["device"]:
        raise ValueError("Complete full-size scene gate is required")
    for check in full["checks"]:
        if not check["fullPixelsPassed"] or not check["stableListAndBinsPassed"]:
            raise ValueError("Full-size output contract failed")
        if sha(args.full_scenes / (check["atlasSha256"] + ".rgba")) != check["atlasSha256"]:
            raise ValueError("Full-size GPU capture changed")
    for case, arm in itertools.product(CASES, ARMS):
        p = read(args.discovery / (case + "-" + arm) / "result.json")
        if not p["passed"] or not p["performanceEligible"]: raise ValueError("Discovery failure or ineligible rehearsal")
        if not (args.discovery / (case + "-" + arm) / "diagnostic.json").is_file(): raise ValueError("Missing separate stage diagnostic")
    rows = [[0, 1, 3, 2], [1, 2, 0, 3], [2, 3, 1, 0], [3, 0, 2, 1]]
    schedule = []
    for round_index in range(8):
        for case_index in range(4):
            case = CASES[(case_index + round_index) % 4]
            for position, arm_index in enumerate(rows[(round_index + CASES.index(case)) % 4]):
                schedule.append({"round": round_index + 1, "case": case, "position": position + 1, "arm": ARMS[arm_index]})
    refs = {p.name: sha(p) for p in sorted(args.references.iterdir()) if p.suffix in {".json", ".rgba"}}
    plan = {"schemaVersion": 1, "registeredUtc": now(), "identity": current, "referenceHashes": refs,
            "correctnessSha256": sha(args.validation / "correctness.json"), "device": correct["device"],
            "fullSceneGateSha256": sha(args.full_scenes / "full-scenes.json"),
            "runtime": read(args.validation / "runtime.json"),
            "arms": ARMS, "cases": CASES, "independentProcessesPerCaseArm": 8, "schedule": schedule,
            "measuredRequestsPerProcess": 8, "warmupRequests": 3, "firstUseRequests": 1,
            "requestsPerLifetime": [1, 12], "primaryCase": "large", "primaryMetric": "mean CPU completed render/readback/raw RGBA export latency per 12-frame request",
            "secondary": ["first use", "actual complete 12-request lifecycle", "GPU render", "GPU readback", "host/committed memory"],
            "comparisons": "All six paired arm contrasts, by case; process means are independent units. No inner pseudo-replication.",
            "interval": "nominal two-sided Student t df=7; log paired ratios; no multiplicity adjustment",
            "benefitRule": "A specific pair is supported only if its 95% paired ratio interval excludes 1; no universal default promotion. Retain adverse cases and first-use cost.",
            "resourceCapBytes": 512 * 1024**2, "preflight": "CPU <=25%, GPU <=15%, free VRAM >=8GiB, free D >=30GiB; shared campaign mutex externally held",
            "failures": "Any wrong output, missing process, source/binary/reference/device drift, device loss or load-gate failure stops confirmation. No exclusions or replacement processes.",
            "scope": "Existing controlled compute renderer, CPU RGBA export; no presentation/Unity/production or cross-hardware claim"}
    save(args.output / "plan.json", plan)


def confirm(args):
    plan = read(args.plan)
    if identity() != plan["identity"]: raise ValueError("Frozen identity drift")
    for filename, expected in plan["referenceHashes"].items():
        if sha(args.references / filename) != expected: raise ValueError("Frozen reference drift")
    save(args.output / "plan.json", plan)
    receipts = []
    for cell in plan["schedule"]:
        if identity() != plan["identity"]: raise ValueError("Source/binary drift before process")
        metadata = "confirmation-" + cell["case"] + ".json"
        if sha(args.references / metadata) != plan["referenceHashes"][metadata]: raise ValueError("Frozen case metadata drift")
        name = f"r{cell['round']:02d}-{cell['case']}-{cell['position']}-{cell['arm']}"
        receipt = launch(args.dotnet, args.references, "confirmation-" + cell["case"], cell["arm"], args.output / name)
        result = read(args.output / name / "result.json")
        if result["device"] != plan["device"]: raise ValueError("Device identity drift")
        if result["runtime"] != plan["runtime"]: raise ValueError("Loaded compiler/runtime identity drift")
        receipts.append({**cell, "directory": name, **receipt})
        save(args.output / "processes.json", receipts)
    save(args.output / "completion.json", {"passed": True, "processCount": len(receipts), "endedUtc": now(), "identity": identity()})


def interval(values, t=2.364624251):
    mean = statistics.mean(values)
    half = t * statistics.stdev(values) / math.sqrt(len(values))
    return {"mean": mean, "lower95": mean - half, "upper95": mean + half}


def analyze(args):
    folder = args.confirmation
    plan = read(folder / "plan.json"); receipts = read(folder / "processes.json")
    if not read(folder / "completion.json")["passed"] or len(receipts) != len(plan["schedule"]):
        raise ValueError("Incomplete matrix")
    if [{k: r[k] for k in ["round", "case", "position", "arm"]} for r in receipts] != plan["schedule"]:
        raise ValueError("Schedule mismatch")
    if len({(r["pid"], r["startedUtc"]) for r in receipts}) != len(receipts): raise ValueError("Repeated process observation")
    if read(folder / "completion.json")["identity"] != plan["identity"]: raise ValueError("Completion identity drift")
    means, rows, devices = {}, [], []
    previous_end = ""
    for receipt in receipts:
        if receipt["startedUtc"] < previous_end: raise ValueError("Overlapping process windows")
        previous_end = receipt["endedUtc"]
        run = folder / receipt["directory"]
        for name, key in [("result.json", "resultSha256"), ("samples.json", "samplesSha256"), ("compilation.json", "compilationSha256")]:
            if sha(run / name) != receipt[key]: raise ValueError("Modified process artifact: " + name)
        if sha(folder / (receipt["directory"] + ".log")) != receipt["logSha256"]: raise ValueError("Modified process log")
        result = read(run / "result.json")
        if result["pid"] != receipt["pid"] or result["arm"] != receipt["arm"] or result["id"] != "confirmation-" + receipt["case"]:
            raise ValueError("Process identity mismatch")
        if result["samples"] != read(run / "samples.json"): raise ValueError("Conflicting sample copies")
        if [x["request"] for x in result["samples"]] != list(range(12)): raise ValueError("Incomplete request sequence")
        for sample in result["samples"]:
            if sha(run / f"atlas-{sample['request']:02d}.rgba") != sample["actualHash"]:
                raise ValueError("Modified full atlas output")
        if not result["passed"] or not result["performanceEligible"] or result["fullOutputChecks"] != 12 or not result["stableListAndBinsPassed"] or any(not x["fullByteComparisonPassed"] for x in result["samples"]):
            raise ValueError("Invalid correctness or ineligible performance")
        if result["device"] != plan["device"]: raise ValueError("Device mismatch")
        if result["runtime"] != plan["runtime"]: raise ValueError("Loaded runtime mismatch")
        measured = [x for x in result["samples"] if x["phase"] == "measured"]
        if len(measured) != 8: raise ValueError("Wrong inner sample count")
        def m(path):
            values = []
            for sample in measured:
                val = sample
                for key in path: val = val[key]
                if not math.isfinite(val) or val < 0: raise ValueError("Invalid timing")
                values.append(val)
            return statistics.mean(values)
        row = {"round": receipt["round"], "case": receipt["case"], "arm": receipt["arm"], "pid": receipt["pid"],
               "completedMs": m(["completedMilliseconds"]), "gpuRenderMs": m(["timing", "gpuRenderMilliseconds"]),
               "gpuReadbackMs": m(["timing", "gpuReadbackMilliseconds"]), "exportMs": m(["exportMilliseconds"]),
               "firstUseMs": result["firstUseMilliseconds"],
               "lifetime12PerRequestMs": (result["firstUseMilliseconds"] + sum(x["completedMilliseconds"] for x in result["samples"][1:]) + result["cleanupMilliseconds"]) / 12,
               "cleanupMs": result["cleanupMilliseconds"],
               "logicalBytes": result["logicalBytes"], "committedBytes": result["committedBytes"],
               "committedReadbackBytes": result["committedReadbackBytes"], "peakWorkingSetBytes": result["processPeakWorkingSetBytes"]}
        rows.append(row); means[(row["round"], row["case"], row["arm"])] = row
    results, comparisons = [], []
    for case, arm in itertools.product(CASES, ARMS):
        group = [r for r in rows if r["case"] == case and r["arm"] == arm]
        results.append({"case": case, "arm": arm, "processes": len(group),
                        **{metric: interval([r[metric] for r in group]) for metric in ["completedMs", "gpuRenderMs", "gpuReadbackMs", "exportMs", "firstUseMs", "lifetime12PerRequestMs"]},
                        "logicalBytes": group[0]["logicalBytes"], "committedBytes": group[0]["committedBytes"],
                        "committedReadbackBytes": group[0]["committedReadbackBytes"], "maxPeakWorkingSetBytes": max(r["peakWorkingSetBytes"] for r in group)})
    for case in CASES:
        for a, b in itertools.combinations(ARMS, 2):
            logs = [math.log(means[(r, case, a)]["completedMs"] / means[(r, case, b)]["completedMs"]) for r in range(1, 9)]
            ratios = {k: math.exp(v) for k, v in interval(logs).items()}
            comparisons.append({"case": case, "numerator": a, "denominator": b, "pairedCompletedRatio": ratios,
                                "pairedSavedMs": interval([means[(r, case, a)]["completedMs"] - means[(r, case, b)]["completedMs"] for r in range(1, 9)])})
    save(args.output / "analysis.json", {"passed": True, "processCount": len(rows), "fullAtlasChecks": len(rows) * 12,
                                        "results": results, "comparisons": comparisons, "processMeans": rows,
                                        "uncertainty": plan["interval"]})
    import csv
    with (args.output / "process-means.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)
    for row in results: print(row["case"], row["arm"], round(row["completedMs"]["mean"], 4), "ms")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["snapshot", "discover", "freeze", "confirm", "analyze"])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--references", type=Path)
    parser.add_argument("--validation", type=Path)
    parser.add_argument("--full-scenes", type=Path)
    parser.add_argument("--discovery", type=Path)
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--confirmation", type=Path)
    args = parser.parse_args()
    if args.output.exists(): raise ValueError("Choose a new output directory")
    args.output.mkdir(parents=True)
    {"snapshot": snapshot, "discover": discover, "freeze": freeze, "confirm": confirm, "analyze": analyze}[args.mode](args)


if __name__ == "__main__": main()
