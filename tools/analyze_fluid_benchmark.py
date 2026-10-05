"""Summarize one future fluid run; do not infer speedup, capacity or missing GPU timings."""
import argparse
import csv
import json
import math
from pathlib import Path
import statistics


def percentile(values, quantile):
    ordered = sorted(values)
    if not ordered:
        return None
    index = (len(ordered) - 1) * quantile
    lower = int(index)
    fraction = index - lower
    return ordered[lower] * (1 - fraction) + ordered[min(lower + 1, len(ordered) - 1)] * fraction


def summarize(folder):
    folder = Path(folder)
    run = json.loads((folder / "run.json").read_text(encoding="utf-8"))
    gate = json.loads((folder / "correctness.json").read_text(encoding="utf-8"))
    if run.get("schema") != "hlslperf.fluid-scan.run.v1" or gate.get("status") != "passed" or not gate.get("fullOutputValidated"):
        raise ValueError("A complete run and passed full-output correctness receipt are required.")
    first, last = run["firstMeasuredUnityFrame"], run["lastMeasuredUnityFrame"]
    expected = last - first + 1
    if expected != run["settings"]["measureFrames"] or expected < 1:
        raise ValueError("Inconsistent measurement interval.")
    metrics = {key: {"values": [], "frames": set(), "unavailable": 0} for key in
               ("wall_frame", "scan_complete", "count_sort_complete", "spatial_hash_complete", "simulation_complete")}
    if run.get("coreTimingEnabled", False): metrics["scan_core"] = {"values": [], "frames": set(), "unavailable": 0}
    with (folder / "observations.csv").open(encoding="utf-8", newline="") as stream:
        for row in csv.DictReader(stream):
            name = row["metric"]
            if name not in metrics:
                raise ValueError("Unknown metric: " + name)
            source = int(row["source_unity_frame"])
            item = metrics[name]
            if source < first or source > last or source in item["frames"]:
                raise ValueError("Out-of-window or duplicate observation.")
            item["frames"].add(source)
            expected_blocks = 1 if name in ("wall_frame", "simulation_complete") else run["iterationsPerFrame"]
            valid = row["ms"] and row["status"] != "unavailable" and int(row["sample_blocks"]) == expected_blocks
            value = float(row["ms"]) if valid else None
            if value is not None and math.isfinite(value) and value > 0:
                item["values"].append(value)
            else:
                item["unavailable"] += 1
    result = {"schema": "hlslperf.fluid-scan.summary.v1", "correctnessPassed": True,
              "performanceClaim": "none; one process only", "gpuDelayValidated": run.get("gpuDelayValidated", False), "metrics": {}}
    for name, item in metrics.items():
        values = item["values"]
        status = "unavailable" if not values else "incomplete" if len(values) != expected else "observed"
        if name != "wall_frame" and values and not run.get("gpuDelayValidated", False):
            status = "provisional_requires_gpu_delay_review"
        result["metrics"][name] = {
            "status": status, "validSamples": len(values), "expectedSamples": expected,
            "unavailableSamples": item["unavailable"] + expected - len(item["frames"]),
            "meanMs": statistics.mean(values) if values else None,
            "p50Ms": percentile(values, .5), "p95Ms": percentile(values, .95),
        }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.run)
    # Exclusive creation preserves earlier evidence even when a caller repeats a command.
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, allow_nan=False)
        stream.write("\n")
    print(json.dumps(result, indent=2, allow_nan=False))


if __name__ == "__main__":
    main()
