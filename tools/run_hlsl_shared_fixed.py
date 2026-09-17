"""Run one fixed complete-task comparison with explicit background-load scope."""
import argparse
import json
import math
import os
from pathlib import Path
import random
import shutil
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import crowd_build_evidence as ev
import run_crowd_v2 as v2


def quantile(values, p):
    index = (len(values) - 1) * p
    low, high = math.floor(index), math.ceil(index)
    return values[low] + (values[high] - values[low]) * (index - low)


def analyze(protocol, records):
    names, rounds = protocol["arms"], protocol["rounds"]
    if len(records) != len(names) * rounds or len({(r['pid'], r['startedUtc']) for r in records}) != len(records):
        raise ValueError("Missing or reused process")
    grouped = {}
    for name in names:
        rows = sorted((r for r in records if r["key"] == name), key=lambda r: r["round"])
        if [r["round"] for r in rows] != list(range(rounds)) or sorted(r["position"] for r in rows) != list(range(len(names))):
            raise ValueError("Unbalanced rounds or positions")
        grouped[name] = rows
    summaries = {}
    for name, rows in grouped.items():
        summaries[name] = {key: statistics.mean(r["metrics"][key] for r in rows)
                          for key in ("lifetime12PerRequestMs", "firstUseMs", "steadyMeanMs", "cleanupMs", "exportMeanMs")}
        summaries[name]["processWallMeanMs"] = statistics.mean(r["wallMs"] for r in rows)
        summaries[name]["quietBracketsPassed"] = sum(r["before"]["passed"] and r["after"]["passed"] for r in rows)
    def drift(name):
        values = [r["metrics"][protocol["metric"]] for r in grouped[name]]
        return 100 * (statistics.mean(values[-4:]) / statistics.mean(values[:4]) - 1)
    drifts = {name: drift(name) for name in ("rts", "cpu-frame-parallel-w12")}
    stable = all(abs(value) <= protocol["maximumReferenceDriftPercent"] for value in drifts.values())
    candidate = [r["metrics"][protocol["metric"]] for r in grouped[protocol["candidate"]]]
    rng = random.Random(protocol["bootstrapSeed"])
    draws = [[rng.randrange(rounds) for _ in range(rounds)] for _ in range(protocol["bootstrapReplicates"])]
    tail = protocol["familyAlpha"] / (len(names) - 1) / 2
    comparisons = []
    for name in names:
        if name == protocol["candidate"]:
            continue
        baseline = [r["metrics"][protocol["metric"]] for r in grouped[name]]
        ratios = sorted(sum(baseline[i] for i in sample) / sum(candidate[i] for i in sample) for sample in draws)
        ratio = statistics.mean(baseline) / statistics.mean(candidate)
        gain = 100 * (1 - 1 / ratio)
        bounds = [quantile(ratios, tail), quantile(ratios, 1 - tail)]
        comparisons.append(dict(comparator=name, speedup=ratio, latencyReductionPercent=gain,
                                adjustedInterval=bounds, clearsGainAndDrift=stable and bounds[0] > 1 and gain >= protocol["minimumLatencyReductionPercent"]))
    return dict(completed=True, processCount=len(records), fullAtlasChecks=len(records)*12,
                environment=protocol["environment"], referenceDriftPercent=drifts, driftGatePassed=stable,
                arms=summaries, comparisons=comparisons, discardedProcesses=0,
                bestObservedMean=min(summaries, key=lambda name: summaries[name][protocol["metric"]]),
                note="Best observed mean is descriptive. All eight candidate comparisons are retained; uncertainty is conditional on this background-load session.")


def main(args):
    if os.environ.get("HLSL_FIXED_MUTEX_OWNER") is None:
        raise RuntimeError("Launch under the foreground named mutex wrapper")
    protocol = ev.read(args.protocol)
    plan_path = args.plan
    plan = ev.read(plan_path)
    build_path = Path(plan["buildReceipt"]["path"])
    dotnet = ev.read(build_path)["host"]["path"]
    refs = Path(plan["referenceDirectory"])
    v2.verify_plan(plan, dotnet, refs)
    plan["phase"] = "confirmation"
    args.output.mkdir(parents=True, exist_ok=False)
    if shutil.disk_usage(args.output).free < 38 * 2**30:
        raise RuntimeError("Output budget plus disk reserve unavailable")
    rng = random.Random(protocol["orderSeed"])
    order = protocol["arms"].copy()
    rng.shuffle(order)
    offsets = list(range(len(order)))
    rng.shuffle(offsets)
    orders = [order[i:] + order[:i] for i in offsets]
    if protocol["rounds"] != len(orders):
        raise ValueError("One complete balanced square required")
    ev.save(args.output / "frozen.json", dict(protocol=protocol, orders=orders, startedUtc=ev.now(),
            runnerSha256=ev.sha(__file__), protocolSha256=ev.sha(args.protocol), buildHead=plan["buildHead"],
            buildReceipt=plan["buildReceipt"], gateReceipts=plan["gates"], referenceHashes=plan["references"],
            mutexOwner=os.environ["HLSL_FIXED_MUTEX_OWNER"]))
    records = []
    deadline = time.monotonic() + 900
    try:
        for round_id, sequence in enumerate(orders):
            for position, name in enumerate(sequence):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Fixed cohort deadline exceeded")
                arm, workers = ("cpu-frame-parallel", int(name.rsplit("-w", 1)[1])) if name.startswith("cpu-") else (name, None)
                before = v2.load_gate()
                if float(before["gpu"][3]) < 8192 or shutil.disk_usage(args.output).free < 30 * 2**30:
                    raise RuntimeError("Memory or disk reserve unavailable")
                folder = args.output / f"round-{round_id:02d}" / name
                folder.parent.mkdir(exist_ok=True)
                command = [dotnet, str(ev.DLL), "run", str(ROOT), str(folder), str(refs), protocol["case"], arm]
                if workers is not None:
                    command.append(str(workers))
                started = time.perf_counter()
                with (folder.parent / (name + ".log")).open("w") as log:
                    child = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                    try:
                        code = child.wait(timeout=remaining)
                    except subprocess.TimeoutExpired:
                        child.kill(); child.wait()
                        raise TimeoutError("Only this owned task process was stopped at its deadline")
                wall_ms = 1000 * (time.perf_counter() - started)
                if code:
                    raise RuntimeError(f"Native task failed: {name}, exit {code}")
                after = v2.load_gate()
                ev.verify_result("run", folder)
                result = ev.read(folder / "result.json")
                v2.validate_measurement(result, plan, dict(arm=arm, workers=workers, case="large"))
                records.append(dict(key=name, round=round_id, position=position, pid=result["pid"],
                    startedUtc=result["startedUtc"], output=str(folder), wallMs=wall_ms,
                    before=before, after=after, metrics=v2.process_metrics(result)))
                ev.save(args.output / "records.json", records)
            print(f"Completed {len(records)}/{len(order)*len(orders)} processes", flush=True)
        v2.verify_plan(ev.read(plan_path), dotnet, refs)
        result = analyze(protocol, records)
        ev.save(args.output / "summary.json", result)
        print(json.dumps(result), flush=True)
    except Exception as error:
        ev.save(args.output / "summary.json", dict(completed=False, recordedProcesses=len(records), error=str(error), discardedProcesses=0))
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True, help="Prepared v2 plan bound to the current validated build")
    parser.add_argument("--protocol", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    main(parser.parse_args())
