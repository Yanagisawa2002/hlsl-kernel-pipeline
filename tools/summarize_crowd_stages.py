"""Summarize complete Crowd/VFX caller stages from saved result.json files.

This intentionally reports overlapping GPU and fence intervals side-by-side instead of
adding them. Use it on an extracted evidence directory or any directory containing
complete-task result.json files.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics


def get(row, *path):
    value = row
    for part in path:
        value = value[part]
    if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
        raise ValueError(f"invalid timing at {'.'.join(path)}")
    return float(value)


def mean(values):
    return statistics.mean(values) if values else float("nan")


def summarize_result(result):
    samples = result["samples"]
    steady = samples[4:]
    if result.get("backend") == "d3d12":
        return {
            "gpuRenderMs": mean([get(s, "timing", "gpuRenderMilliseconds") for s in steady]),
            "gpuReadbackMs": mean([get(s, "timing", "gpuReadbackMilliseconds") for s in steady]),
            "cpuRecordMs": mean([get(s, "timing", "cpuRecordMilliseconds") for s in steady]),
            "cpuSubmitMs": mean([get(s, "timing", "submission", "cpuSubmitMilliseconds") for s in steady]),
            "cpuFenceWaitMs": mean([get(s, "timing", "submission", "cpuFenceWaitMilliseconds") for s in steady]),
            "cpuCopyMs": mean([get(s, "timing", "cpuCopyMilliseconds") for s in steady]),
            "exportMs": mean([get(s, "exportMilliseconds") for s in steady]),
            "completedMs": mean([get(s, "completedMilliseconds") for s in steady]),
        }
    if result.get("backend") == "cpu":
        return {
            "cpuRenderMs": mean([get(s, "cpuTiming", "renderMilliseconds") for s in steady]),
            "exportMs": mean([get(s, "exportMilliseconds") for s in steady]),
            "completedMs": mean([get(s, "completedMilliseconds") for s in steady]),
        }
    raise ValueError("unknown result backend")


def main(root: Path):
    rows = []
    for path in root.rglob("result.json"):
        try:
            result = json.loads(path.read_text(encoding="utf-8"))
            if "samples" not in result or result.get("arm") is None:
                continue
            stages = summarize_result(result)
            key = result["arm"]
            if result.get("workers") is not None:
                key += f"-w{result['workers']}"
            rows.append((key, stages, path))
        except (KeyError, ValueError, json.JSONDecodeError):
            continue

    if not rows:
        raise SystemExit("no complete-task result.json files found")

    grouped = {}
    for key, stages, _ in rows:
        grouped.setdefault(key, []).append(stages)

    summary = {}
    for key, arm_rows in sorted(grouped.items()):
        fields = sorted(set().union(*(row.keys() for row in arm_rows)))
        summary[key] = {field: mean([row[field] for row in arm_rows if field in row]) for field in fields}

    print(json.dumps({"processes": len(rows), "arms": summary}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="Extracted evidence/run directory")
    main(parser.parse_args().root)
