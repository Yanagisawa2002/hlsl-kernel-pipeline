"""Bounded, supervised whole-task discovery and one-primary-comparison confirmation.

Prepare plans before measuring. Each invocation launches at most --max-processes
new processes, under the existing campaign lock. Pass prior batch directories
explicitly to continue. A failed observation invalidates its cohort; no process
is killed, replaced or excluded. A preflight failure launches nothing.
"""
from __future__ import annotations

import argparse
import ctypes
import itertools
import json
import math
from pathlib import Path
import statistics
import subprocess
import time
import types

import crowd_build_evidence as ev

GPU_ARMS = ["hierarchical", "fused", "wave-tiled", "rts"]
CASES = ["large", "small", "medium-tail", "dense-tail"]
T95 = {8: 2.364624251, 12: 2.200985160, 16: 2.131449546, 24: 2.068657610, 32: 2.039513446}


def identity(path): return {"path": str(Path(path).resolve()), "sha256": ev.sha(path)}
def arm_key(arm, workers=None): return arm if workers is None else f"{arm}-w{workers}"


def cpu_times():
    idle, kernel, user = ctypes.c_ulonglong(), ctypes.c_ulonglong(), ctypes.c_ulonglong()
    if not ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
        raise OSError("GetSystemTimes failed")
    return idle.value, kernel.value + user.value


def load_gate():
    start = cpu_times(); time.sleep(.5); end = cpu_times()
    total = end[1] - start[1]
    cpu = 100 * (1 - (end[0] - start[0]) / total) if total else 100
    query = subprocess.check_output(["nvidia-smi", "--query-gpu=name,driver_version,utilization.gpu,memory.free,temperature.gpu",
                                     "--format=csv,noheader,nounits"], text=True)
    lines = query.strip().splitlines()
    if len(lines) != 1: raise ValueError("This campaign requires one explicitly identified NVIDIA adapter")
    gpu = [s.strip() for s in lines[0].split(",")]
    return {"utc": ev.now(), "cpuPercent": cpu, "gpu": gpu,
            "passed": cpu <= 25 and float(gpu[2]) <= 15 and float(gpu[3]) >= 8192}


def checked_receipt(path, build):
    receipt = ev.read(path)
    if not receipt.get("passed") or receipt.get("kind") != "bound-check" or receipt["buildReceipt"]["sha256"] != ev.sha(build):
        raise ValueError("Gate is not bound to this successful build")
    if receipt["before"] != receipt["after"]: raise ValueError("Gate identity drift")
    if receipt["process"]["exitCode"] or ev.sha(receipt["process"]["log"]) != receipt["process"]["logSha256"]:
        raise ValueError("Gate process/log changed")
    folder = Path(path).parent / "result"
    if ev.file_manifest(folder) != receipt["artifacts"]: raise ValueError("Gate artifact changed")
    if receipt["mode"] != "cpu-tests": ev.verify_result(receipt["mode"], folder)
    return receipt, folder


def verify_plan(plan, dotnet, refs):
    if plan.get("schemaVersion") != 2 or plan.get("phase") not in ["discovery", "confirmation"]:
        raise ValueError("Version-2 protocol required; the retired matrix is ineligible")
    build = Path(plan["buildReceipt"]["path"])
    if ev.sha(build) != plan["buildReceipt"]["sha256"]: raise ValueError("Build receipt changed")
    ev.verify_build(build, dotnet)
    if ev.file_manifest(refs, {".json", ".rgba"}) != plan["references"]: raise ValueError("Reference identity drift")
    for gate in plan["gates"].values():
        if ev.sha(gate["path"]) != gate["sha256"]: raise ValueError("Bound prerequisite changed")
        checked_receipt(gate["path"], build)


def prepare(args):
    build = ev.verify_build(args.build_receipt, args.dotnet)
    names = {"cpu-tests": args.cpu_tests, "debug-control": args.debug_control, "validate": args.validation,
             "check-scenes": args.full_scenes, "check-cpu": args.cpu_check,
             "cpu-rehearsal": args.cpu_rehearsal, "gpu-rehearsal": args.gpu_rehearsal}
    folders = {}
    for name, path in names.items():
        receipt, folder = checked_receipt(path, args.build_receipt)
        if receipt["mode"] != ("rehearse" if name.endswith("-rehearsal") else name): raise ValueError("Incorrect gate mode: " + name)
        folders[name] = folder
    control = ev.read(folders["debug-control"] / "debug-control.json")
    for name in ["validate", "check-scenes"]:
        for row in ev.read(folders[name] / "debug.json")["snapshots"]:
            for f in ["storageFilter", "retrievalFilter"]:
                if row["snapshot"][f] != control["initial"][f]: raise ValueError("Workload filter differs from injection controls")
    cpu = ev.read(folders["check-cpu"] / "cpu-check.json")
    maximum = cpu["maximumWorkers"]
    for name, expected_arm in [("cpu-rehearsal", "cpu-frame-parallel"), ("gpu-rehearsal", "wave-tiled")]:
        rehearsal = ev.read(folders[name] / "result.json")
        if rehearsal["arm"] != expected_arm or rehearsal["id"] != "discovery-large" or rehearsal["performanceEligible"]:
            raise ValueError("Required complete-caller rehearsal is absent")
        if name == "cpu-rehearsal" and rehearsal["workers"] != maximum: raise ValueError("Highest reasonable CPU worker count was not exercised")
    workers = sorted({n for n in [1, 2, 4, 8, 12, maximum] if n <= maximum})
    arms = [{"arm": a, "workers": None} for a in GPU_ARMS] + [{"arm": "cpu-frame-parallel", "workers": n} for n in workers]
    schedule = []
    # Primary case first; reverse the complete order for the second process round.
    # This is exploratory ordering. Confirmation uses separately balanced AB/BA pairs.
    for case in CASES:
        for round_id in [1, 2]:
            for position, arm in enumerate(arms if round_id == 1 else list(reversed(arms)), 1):
                schedule.append({"index": len(schedule), "case": case, "round": round_id, "position": position, **arm})
    plan = {"schemaVersion": 2, "phase": "discovery", "createdUtc": ev.now(), "buildReceipt": identity(args.build_receipt),
            "buildHead": build["sourceAfter"]["head"], "gates": {k: identity(v) for k, v in names.items()},
            "references": ev.file_manifest(args.references, {".json", ".rgba"}), "referenceDirectory": str(args.references.resolve()),
            "cpuMachine": cpu["cpuMachine"], "cpuWorkers": workers, "maximumReasonableCpuWorkers": maximum,
            "device": ev.read(folders["validate"] / "correctness.json")["device"],
            "gpuRuntime": ev.read(folders["validate"] / "runtime.json"), "schedule": schedule,
            "candidate": {"arm": "wave-tiled", "workers": None}, "primaryCase": "large", "primaryMetric": "lifetime12PerRequestMs",
            "resourceBudgetBytes": 2 * 1024**3, "resourceBudgetIsCallerConstraint": False,
            "resourceRule": "Shared immutable CPU caches; all 1/2/4/8/12 feasible worker choices include the CPU maximum. No automatic worker reduction. Report measured logical storage and peak working set.",
            "stopping": "At most max-processes per invocation. Fail before launch on load; any failed observation invalidates the cohort. Retain every attempt.",
            "loadRule": "Campaign queue/mutex/free host RAM/disk gate externally; two CPU<=25%/GPU<=15% snapshots before and one after each process",
            "scope": "Controlled CPU-input-to-buffered-RGBA-export task; no presentation, durable flush, Unity, cold OS cache or cross-hardware claim"}
    ev.save(args.output / "plan.json", plan)
    print("Prepared discovery:", len(schedule), "processes total; bounded invocations only; CPU workers", workers)


def validate_measurement(result, plan, cell):
    if result["arm"] != cell["arm"] or result.get("workers") != cell["workers"] or result["id"] != plan["phase"] + "-" + cell["case"]:
        raise ValueError("Process arm/worker/case drift")
    if result["cpuMachine"] != plan["cpuMachine"]: raise ValueError("CPU/OS/runtime identity drift")
    if cell["arm"] in GPU_ARMS and result["device"] != plan["device"]: raise ValueError("GPU identity drift")
    expected = {r["name"].lower(): r for r in plan["gpuRuntime"]}
    actual = {r["name"].lower(): r for r in result["runtime"]}
    required = set(expected) if cell["arm"] in GPU_ARMS else {"coreclr.dll", "hostfxr.dll"}
    if set(actual) != required or any(actual[k] != expected[k] for k in required): raise ValueError("Native compiler/runtime drift")
    if result["allocationCapBytes"] != plan["resourceBudgetBytes"]: raise ValueError("Resource budget drift")
    logical = result["logicalBytes"] if cell["arm"] in GPU_ARMS else result["hostLogicalBytes"]
    if logical <= 0 or logical > plan["resourceBudgetBytes"]: raise ValueError("Resource budget exceeded")
    process_metrics(result)


def process_metrics(result):
    samples = result["samples"]
    if [s["request"] for s in samples] != list(range(12)): raise ValueError("Incomplete request sequence")
    expected_phases = ["first-use"] + ["warmup"] * 3 + ["measured"] * 8
    if [s["phase"] for s in samples] != expected_phases: raise ValueError("Ineligible rehearsal or request phases")
    def positive(value):
        if type(value) not in (float, int) or not math.isfinite(value) or value < 0: raise ValueError("Invalid timing")
        return value
    first = positive(result["firstUseMilliseconds"]); cleanup = positive(result["cleanupMilliseconds"])
    completed = [positive(s["completedMilliseconds"]) for s in samples]
    if first < completed[0] or any(v == 0 for v in completed): raise ValueError("Invalid first-use or completed duration")
    exports = [positive(s["exportMilliseconds"]) for s in samples]
    if any(e > c for e, c in zip(exports, completed)): raise ValueError("Export exceeds enclosing request")
    stage_means = {}
    if result.get("backend") == "d3d12":
        paths = {"gpuRenderMs": ["timing", "gpuRenderMilliseconds"], "gpuReadbackMs": ["timing", "gpuReadbackMilliseconds"],
                 "cpuRecordMs": ["timing", "cpuRecordMilliseconds"], "cpuCopyMs": ["timing", "cpuCopyMilliseconds"],
                 "cpuSubmitMs": ["timing", "submission", "cpuSubmitMilliseconds"],
                 "cpuFenceWaitMs": ["timing", "submission", "cpuFenceWaitMilliseconds"]}
    elif result.get("backend") == "cpu": paths = {"cpuRenderMs": ["cpuTiming", "renderMilliseconds"]}
    else: paths = {}  # Minimal mathematical fixtures; real process validation requires a known arm/backend.
    for name, path in paths.items():
        values = []
        for sample in samples:
            value = sample
            for part in path: value = value[part]
            values.append(positive(value))
        stage_means[name] = statistics.mean(values[4:])
    lifetime = (first + sum(completed[1:]) + cleanup) / 12
    return {"lifetime12PerRequestMs": lifetime, "firstUseMs": first, "steadyMeanMs": statistics.mean(completed[4:]),
            "cleanupMs": cleanup, "exportMeanMs": statistics.mean(exports[4:]),
            "peakWorkingSetBytes": result["processPeakWorkingSetBytes"],
            "hostLogicalBytes": result.get("hostLogicalBytes", result.get("hostInputAndOutputBytes")),
            "deviceLogicalBytes": result.get("logicalBytes"), "committedDeviceBytes": result.get("committedBytes"),
            "readbackBytes": result.get("committedReadbackBytes"), "cpuWorkers": result.get("workers"), "stageMeans": stage_means}


def collect_batches(plan, plan_path, batches, complete=False):
    jobs = []
    for batch_path in batches:
        batch = ev.read(batch_path / "batch.json")
        if batch["planSha256"] != ev.sha(plan_path): raise ValueError("Batch plan mismatch")
        if not batch["passed"] and not (plan["phase"] == "discovery" and batch.get("stoppedBeforeLaunch")):
            raise ValueError("Failed observation invalidates cohort; no replacement or exclusions")
        for job in batch["jobs"]:
            cell = plan["schedule"][len(jobs)]
            if job["cell"] != cell: raise ValueError("Missing, duplicated or reordered process")
            path = batch_path / job["directory"] / "check-receipt.json"
            if ev.sha(path) != job["receiptSha256"]: raise ValueError("Modified process receipt")
            receipt, folder = checked_receipt(path, plan["buildReceipt"]["path"])
            if receipt["mode"] != "run" or not job["postflight"]["passed"] or not all(x["passed"] for x in job["preflight"]):
                raise ValueError("Ineligible timing process")
            if (not receipt["performancePreflight"]["passed"] or not receipt["performancePostflight"]["passed"] or
                receipt["protocol"]["sha256"] != ev.sha(plan_path) or receipt["protocol"]["cell"] != cell):
                raise ValueError("Process lacks its immediate load gate or protocol binding")
            result = ev.read(folder / "result.json")
            validate_measurement(result, plan, cell)
            if result["pid"] != receipt["process"]["pid"]: raise ValueError("Child PID mismatch")
            if jobs and receipt["process"]["startedUtc"] < jobs[-1]["endedUtc"]: raise ValueError("Overlapping process observations")
            jobs.append({**cell, "key": arm_key(cell["arm"], cell["workers"]), "pid": result["pid"],
                         "startedUtc": receipt["process"]["startedUtc"], "endedUtc": receipt["process"]["endedUtc"],
                         "receipt": identity(path), **process_metrics(result)})
    if complete and len(jobs) != len(plan["schedule"]): raise ValueError("Incomplete cohort")
    if len({(j["pid"], j["startedUtc"]) for j in jobs}) != len(jobs): raise ValueError("Repeated process observation")
    return jobs


def execute(args):
    plan = ev.read(args.plan)
    if plan["phase"] != args.mode: raise ValueError("Wrong execution phase")
    verify_plan(plan, args.dotnet, args.references)
    ev.require_lock("performance")
    prior = collect_batches(plan, args.plan, args.batches)
    if args.max_processes < 1: raise ValueError("Bounded process count must be positive")
    if args.mode == "confirmation" and (args.max_processes % 2 or len(prior) % 2): raise ValueError("Keep each confirmation pair in one invocation")
    batch = {"schemaVersion": 2, "planSha256": ev.sha(args.plan), "phase": plan["phase"], "passed": False,
             "startedUtc": ev.now(), "jobs": [], "stoppedBeforeLaunch": False, "priorBatches": [identity(p / "batch.json") for p in args.batches]}
    target = args.output / "batch.json"
    try:
        for cell in plan["schedule"][len(prior):len(prior) + args.max_processes]:
            pre = [load_gate(), load_gate()]
            if not all(s["passed"] for s in pre):
                batch.update(stoppedBeforeLaunch=True, failedCell=cell, failedPreflight=pre)
                raise ValueError("Fresh load gate failed; no new process launched")
            name = f"{cell['index']:03d}-{cell['case']}-{arm_key(cell['arm'], cell['workers'])}"
            directory = args.output / name; directory.mkdir()
            batch["activeCell"] = cell; batch["activeDirectory"] = name
            check = types.SimpleNamespace(output=directory, dotnet=args.dotnet, build_receipt=Path(plan["buildReceipt"]["path"]),
                check="run", references=args.references, case=plan["phase"] + "-" + cell["case"], arm=cell["arm"], workers=cell["workers"],
                protocol=args.plan, protocol_index=cell["index"])
            ev.check(check)
            post = ev.read(directory / "check-receipt.json")["performancePostflight"]
            job = {"cell": cell, "directory": name, "receiptSha256": ev.sha(directory / "check-receipt.json"),
                   "preflight": pre, "postflight": post}
            batch["jobs"].append(job); ev.save(target, batch)
            if not post["passed"]: raise ValueError("Post-process interference gate failed; retain and invalidate this cohort")
            validate_measurement(ev.read(directory / "result/result.json"), plan, cell)
            batch.pop("activeCell"); batch.pop("activeDirectory")
            print("Eligible process", cell["index"], cell["case"], arm_key(cell["arm"], cell["workers"]), flush=True)
        batch["passed"] = True
    except Exception as error:
        batch["error"] = str(error)
        raise
    finally:
        batch["endedUtc"] = ev.now(); batch["nextIndex"] = len(prior) + len(batch["jobs"])
        batch["cohortComplete"] = batch["passed"] and batch["nextIndex"] == len(plan["schedule"])
        ev.save(target, batch)


def choose_comparator(rows):
    primary = [r for r in rows if r["case"] == "large"]
    keys = list(dict.fromkeys(r["key"] for r in primary if r["key"] != "wave-tiled"))
    if not any(k.startswith("cpu-frame-parallel-w") for k in keys): raise ValueError("CPU alternative absent")
    scores = {k: statistics.median(r["lifetime12PerRequestMs"] for r in primary if r["key"] == k) for k in keys}
    return min(keys, key=lambda k: (scores[k], keys.index(k))), scores


def confirmation_schedule(candidate, comparator, rounds):
    if rounds not in T95: raise ValueError("Unsupported predeclared process count")
    result = []
    for case in CASES:
        for round_id in range(1, (rounds if case == "large" else 2) + 1):
            for position, arm in enumerate([candidate, comparator] if round_id % 2 else [comparator, candidate], 1):
                result.append({"index": len(result), "case": case, "round": round_id, "position": position, **arm})
    return result


def freeze(args):
    discovery = ev.read(args.plan)
    if discovery["phase"] != "discovery": raise ValueError("Complete discovery required")
    verify_plan(discovery, args.dotnet, args.references)
    rows = collect_batches(discovery, args.plan, args.batches, complete=True)
    key, scores = choose_comparator(rows)
    row = next(r for r in rows if r["key"] == key)
    comparator = {"arm": row["arm"], "workers": row["workers"]}
    logs = []
    for round_id in [1, 2]:
        pair = {r["key"]: r["lifetime12PerRequestMs"] for r in rows if r["case"] == "large" and r["round"] == round_id}
        logs.append(math.log(pair["wave-tiled"] / pair[key]))
    sd = statistics.stdev(logs)
    # Precision planning from two pilot pairs is uncertain; record the estimate.
    # Pick from a fixed grid before confirmation. Never increase n after seeing its data.
    required = max(8, math.ceil((2.4 * sd / math.log(1.05)) ** 2))
    available = [n for n in T95 if n >= required]
    if not available: raise ValueError("Pilot variance exceeds the declared 32-pair budget; register a revised design before collecting confirmation")
    rounds = min(available)
    plan = {**discovery, "phase": "confirmation", "registeredUtc": ev.now(), "discoveryPlan": identity(args.plan),
            "discoveryBatches": [identity(p / "batch.json") for p in args.batches], "comparator": comparator,
            "comparatorScoresMs": scores, "primaryPairs": rounds, "pilotLogSd": sd, "pilotRequestedPairs": required,
            "sampleSizeRule": "Next of 8/12/16/24/32 above ceil((2.4*pilot_log_sd/log(1.05))^2); two-pair pilot uncertainty retained. Fixed before confirmation; no extensions or early success stopping.",
            "schedule": confirmation_schedule(discovery["candidate"], comparator, rounds),
            "confirmatoryHypotheses": 1, "interval": "Paired log lifetime-cost ratio, two-sided 95% Student t on process pairs",
            "secondaryRule": "All non-large cases and other metrics are descriptive only; no nominal interval supports another benefit claim",
            "stopping": "Fixed complete schedule. Any failed observation or load/identity/output gate stops and invalidates the attempt. No exclusions, replacement observations or optional stopping."}
    ev.save(args.output / "plan.json", plan); ev.save(args.output / "discovery-processes.json", rows)
    print("Registered", rounds, "primary pairs; comparator", key, "and", len(plan["schedule"]), "total processes")


def paired_interval(values):
    n = len(values)
    if n not in T95 or not all(math.isfinite(v) for v in values): raise ValueError("Unregistered pair count or invalid observation")
    mean = statistics.mean(values); half = T95[n] * statistics.stdev(values) / math.sqrt(n)
    return {"mean": mean, "lower95": mean - half, "upper95": mean + half}


def analyze(args):
    plan = ev.read(args.plan)
    if plan.get("schemaVersion") != 2 or plan.get("phase") != "confirmation" or plan.get("confirmatoryHypotheses") != 1:
        raise ValueError("One registered primary hypothesis is required")
    rows = collect_batches(plan, args.plan, args.batches, complete=True)
    a, b = "wave-tiled", arm_key(plan["comparator"]["arm"], plan["comparator"]["workers"])
    ratios, saved = [], []
    for r in range(1, plan["primaryPairs"] + 1):
        pair = {x["key"]: x["lifetime12PerRequestMs"] for x in rows if x["case"] == "large" and x["round"] == r}
        ratios.append(math.log(pair[a] / pair[b])); saved.append(pair[b] - pair[a])
    ratio = {k: math.exp(v) for k, v in paired_interval(ratios).items()}
    descriptive = []
    for case, key in itertools.product(CASES, [a, b]):
        group = [r for r in rows if r["case"] == case and r["key"] == key]
        descriptive.append({"case": case, "arm": key, "processes": len(group), "descriptiveOnly": True,
            "stageMeans": {m: statistics.mean(r["stageMeans"][m] for r in group) for m in group[0]["stageMeans"]},
            **{m: {"mean": statistics.mean(r[m] for r in group), "min": min(r[m] for r in group), "max": max(r[m] for r in group)}
               for m in ["lifetime12PerRequestMs", "firstUseMs", "steadyMeanMs", "peakWorkingSetBytes"]}})
    ev.save(args.output / "analysis.json", {"passed": True, "plan": identity(args.plan), "processCount": len(rows),
        "primary": {"case": "large", "candidate": a, "comparator": b, "pairs": plan["primaryPairs"],
                    "candidateOverComparatorRatio": ratio, "savedMs": paired_interval(saved), "benefitSupported": ratio["upper95"] < 1,
                    "candidateSlowerSupported": ratio["lower95"] > 1},
        "descriptive": descriptive, "processes": rows, "scope": plan["scope"],
        "limitations": "Pilot-based sample size and normality of paired log costs; idle snapshots cannot exclude all transient desktop activity. No cold OS cache or universal application claim."})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["prepare", "discovery", "freeze", "confirmation", "analyze"])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet")
    parser.add_argument("--references", type=Path)
    parser.add_argument("--build-receipt", type=Path)
    for gate in ["cpu-tests", "debug-control", "validation", "full-scenes", "cpu-check", "cpu-rehearsal", "gpu-rehearsal"]: parser.add_argument("--" + gate, type=Path)
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--batches", type=Path, nargs="*", default=[])
    parser.add_argument("--max-processes", type=int, default=2)
    args = parser.parse_args(); args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    {"prepare": prepare, "discovery": execute, "freeze": freeze, "confirmation": execute, "analyze": analyze}[args.mode](args)


if __name__ == "__main__": main()
