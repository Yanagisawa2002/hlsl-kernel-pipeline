"""Summarize stage telemetry already emitted by the Crowd complete-task runner.

This script does not rerun or reinterpret the benchmark. It consumes records.json
from run_hlsl_shared_fixed.py, validates the expected metric shape, and emits a
compact stage-breakdown JSON/Markdown pair. GPU execution and CPU fence wait are
reported side-by-side but never summed because they overlap by construction.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics


def read(path: Path):
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def finite_nonnegative(value, label):
    if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
        raise ValueError(f"Invalid {label}: {value!r}")
    return float(value)


def mean(values):
    return statistics.mean(values) if values else None


def aggregate(records):
    if not isinstance(records, list) or not records:
        raise ValueError("records.json must contain at least one process record")
    grouped = {}
    process_keys = set()
    for record in records:
        key = record.get("key")
        metrics = record.get("metrics")
        identity = (record.get("pid"), record.get("startedUtc"))
        if not isinstance(key, str) or not isinstance(metrics, dict):
            raise ValueError("Each record needs key and metrics")
        if identity in process_keys:
            raise ValueError("Duplicate process identity")
        process_keys.add(identity)
        grouped.setdefault(key, []).append(record)

    arms = {}
    for key, rows in sorted(grouped.items()):
        stage_names = sorted({
            stage
            for row in rows
            for stage in row["metrics"].get("stageMeans", {})
        })
        stage_means = {}
        for stage in stage_names:
            values = []
            for row in rows:
                stages = row["metrics"].get("stageMeans", {})
                if stage not in stages:
                    raise ValueError(f"Stage {stage} missing from one {key} process")
                values.append(finite_nonnegative(stages[stage], f"{key}.{stage}"))
            stage_means[stage] = mean(values)

        core = {}
        for metric in ("lifetime12PerRequestMs", "firstUseMs", "steadyMeanMs", "cleanupMs", "exportMeanMs"):
            core[metric] = mean([
                finite_nonnegative(row["metrics"][metric], f"{key}.{metric}")
                for row in rows
            ])
        arms[key] = {
            "processCount": len(rows),
            **core,
            "stageMeans": stage_means,
        }

    wave = arms.get("wave-tiled")
    cpu12 = arms.get("cpu-frame-parallel-w12")
    diagnosis = None
    if wave and cpu12:
        gpu = wave["stageMeans"]
        cpu = cpu12["stageMeans"]
        diagnosis = {
            "eligibleInterpretation": "Measured contract only: CPU-input to buffered RGBA export.",
            "steadyGapMs": wave["steadyMeanMs"] - cpu12["steadyMeanMs"],
            "lifecycleGapMs": wave["lifetime12PerRequestMs"] - cpu12["lifetime12PerRequestMs"],
            "waveGpuRenderMs": gpu.get("gpuRenderMs"),
            "waveGpuReadbackMs": gpu.get("gpuReadbackMs"),
            "waveCpuFenceWaitMs": gpu.get("cpuFenceWaitMs"),
            "waveExportMs": wave["exportMeanMs"],
            "cpu12RenderMs": cpu.get("cpuRenderMs"),
            "cpu12ExportMs": cpu12["exportMeanMs"],
            "overlapWarning": "gpuRenderMs and cpuFenceWaitMs overlap and must not be added as independent costs.",
        }
    return {
        "schemaVersion": "1.0",
        "recordCount": len(records),
        "arms": arms,
        "waveVsCpu12": diagnosis,
    }


def fmt(value):
    return "—" if value is None else f"{value:.3f}"


def markdown(summary):
    lines = [
        "# Crowd complete-task stage breakdown",
        "",
        "Generated from the existing per-process telemetry. No benchmark samples are discarded or replaced.",
        "",
        "| Arm | Lifecycle ms/request | First use ms | Steady ms | Render ms | Readback ms | Fence wait ms | Export ms |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for key, arm in summary["arms"].items():
        stages = arm["stageMeans"]
        render = stages.get("gpuRenderMs", stages.get("cpuRenderMs"))
        lines.append(
            f"| {key} | {fmt(arm['lifetime12PerRequestMs'])} | {fmt(arm['firstUseMs'])} | "
            f"{fmt(arm['steadyMeanMs'])} | {fmt(render)} | {fmt(stages.get('gpuReadbackMs'))} | "
            f"{fmt(stages.get('cpuFenceWaitMs'))} | {fmt(arm['exportMeanMs'])} |"
        )
    lines += [
        "",
        "> GPU render and CPU fence-wait timings overlap. Do not sum them into a synthetic waterfall.",
        "",
    ]
    diagnosis = summary.get("waveVsCpu12")
    if diagnosis:
        lines += [
            "## Wave-tiled GPU vs CPU12",
            "",
            f"- Complete lifecycle gap: **{diagnosis['lifecycleGapMs']:.3f} ms/request**.",
            f"- Reused-request gap: **{diagnosis['steadyGapMs']:.3f} ms/request**.",
            f"- Wave GPU render: **{fmt(diagnosis['waveGpuRenderMs'])} ms**; readback: **{fmt(diagnosis['waveGpuReadbackMs'])} ms**; export: **{fmt(diagnosis['waveExportMs'])} ms**.",
            f"- CPU12 render: **{fmt(diagnosis['cpu12RenderMs'])} ms**; export: **{fmt(diagnosis['cpu12ExportMs'])} ms**.",
            "- Interpretation is deliberately scoped to the measured CPU-input → buffered-export caller.",
            "",
        ]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("records", type=Path, help="records.json emitted by run_hlsl_shared_fixed.py")
    parser.add_argument("--json", type=Path, required=True, help="output summary JSON")
    parser.add_argument("--markdown", type=Path, required=True, help="output Markdown report")
    args = parser.parse_args()

    summary = aggregate(read(args.records))
    args.json.parent.mkdir(parents=True, exist_ok=True)
    args.markdown.parent.mkdir(parents=True, exist_ok=True)
    args.json.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    args.markdown.write_text(markdown(summary) + "\n", encoding="utf-8")
    print(json.dumps(summary["waveVsCpu12"], indent=2))


if __name__ == "__main__":
    main()
