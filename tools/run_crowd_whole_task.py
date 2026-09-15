"""Retired version-1 whole-task protocol and descriptive evidence audit.

Discovery, freeze and confirmation now reject all calls before launching any process.

No background service, scheduler, cache clearing, settings changes or process killing.
Every invocation requires a fresh evidence directory. Failed attempts are retained.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import statistics
import subprocess
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
                                "tools/run_crowd_whole_task.py", "tools/crowd_build_evidence.py", "tools/Invoke-CrowdWholeTaskLocked.ps1"]]
    binary = [p for p in DLL.parent.rglob("*") if p.is_file() and p.suffix in {".dll", ".exe", ".json"}]
    return {"source": {p.relative_to(ROOT).as_posix(): sha(p) for p in sorted(set(paths))},
            "binary": {p.relative_to(DLL.parent).as_posix(): sha(p) for p in sorted(binary)}}


def retired_protocol(args):
    raise ValueError("The version-1 four-GPU/128-process protocol is retired. "
                     "Use run_crowd_v2.py with bound CPU/GPU gates and a version-2 protocol; these "
                     "legacy commands cannot launch new processes. See CROWD_PROTOCOL_V2.md.")


def discover(args): retired_protocol(args)
def freeze(args): retired_protocol(args)
def confirm(args): retired_protocol(args)


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
                                        "uncertainty": plan["interval"], "inferentialClaimsEligible": False,
                                        "status": "retired-v1-descriptive-audit-only"})
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
    if args.mode in {"discover", "freeze", "confirm"}: retired_protocol(args)
    if args.output.exists(): raise ValueError("Choose a new output directory")
    args.output.mkdir(parents=True)
    {"snapshot": snapshot, "discover": discover, "freeze": freeze, "confirm": confirm, "analyze": analyze}[args.mode](args)


if __name__ == "__main__": main()
