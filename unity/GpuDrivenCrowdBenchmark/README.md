# Controlled CPU-driven / GPU-resident Crowd benchmark

Read [PROTOCOL.md](PROTOCOL.md) before running. This dedicated workload leaves the live sample and the September 16 complete-task benchmark unchanged. It measures a managed all-agent CPU-single baseline and the append/count-copy GPU path with the same deterministic population, trajectory, quad shader and offscreen target.

## Build (Windows, Unity 6000.3.13f1)

From the repository root, with Python and the Unity Windows standalone module installed:

```powershell
python tools/prepare_crossover_project.py --project D:/CodexValidation/crossover-project
$env:CROSSOVER_PLAYER_PATH = 'D:/CodexValidation/crossover-player/Crossover.exe'
& 'C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Unity.exe' -batchmode -quit -projectPath D:/CodexValidation/crossover-project -executeMethod HlslPerf.Crossover.BuildCrossover.Build -logFile D:/CodexValidation/crossover-build.log
```

Check build exit, log and `Crossover.exe.build.json`. The generated project/player stay outside the repository. The build embeds a hash of the benchmark C#/shader sources; source changes require rebuilding and a new evidence directory. GPU Recorder requires the Development player; no attached profiler, deep profiling or script debugger is enabled.

## Run stages

Run **serially** with an idle GPU. The runner launches hidden standalone processes while retaining D3D12 graphics; it never uses `-nographics`. Commands default to the full 18-cell matrix, 300 warmup and 1000 measured frames. For initial smoke add `--agents 100000 --densities 0.25` to each stage.

```powershell
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-evidence --stage calibrate
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-evidence --stage validation
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-evidence --stage pilot --agents 100000 --densities 0.25
```

Review pilot output for unavailable GPU queries, pacing, noise/drift and correctness. An error is a stop, not permission to continue collecting timings. After methodology is valid, the following collects **resource-cost** measurements, not an automatic end-to-end crossover:

```powershell
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-evidence --stage measure
python tools/analyze_crossover.py D:/CodexValidation/crossover-evidence --output D:/CodexValidation/crossover-summary
```

The analyzer emits summary JSON, Markdown and CSV. Add `--plots` (with matplotlib installed) for resource-cost plots A/C; it explicitly omits an architecture-ratio plot because no eligible critical-path metric exists.

The full matrix uses six balanced pairs per condition (216 separate measurement processes), with conditions interleaved across rounds. The runner refuses overwrite, records command/exit/adapter snapshots, and stops on failed gates. Do not mix binaries, calibrations, interrupted runs or pilot files. Keep failed receipts as evidence and version a corrected experiment separately. Generated Unity logs can contain local paths/identifiers; review before publishing.

Direct invocation is also supported:

```text
Crossover.exe -batchmode -force-d3d12 --mode cpu --agents 1000000 --density 0.25 --seed 69501203 --warmup-frames 300 --frames 1000 --calibration frozen.json --run-id unique-id --pair-id condition-pair0 --output result.json
```

Use `--mode gpu` for GPU-resident rendering and `--mode validation` for separate set/image checks. `--cpu-workers 1` is accepted; other worker counts are rejected. `calibrate` produces the required frozen views/counts file. Unity single-dash flags are allowed; unknown/duplicate double-dash benchmark options fail.

## Metrics and schema

Raw JSON schema 1 contains source/calibration identities, runtime adapter/API/driver, process/pair IDs, options, full per-frame samples and validation checks. Each measured sample has CPU fused cull/list, CPU upload, submission and total durations, plus GPU cull, draw and command-range durations. GPU cull includes counter reset/copy. CPU list work is fused with the predicate; it is not falsely assigned a separate duration.

`-1` marks unavailable GPU timing, with `gpuTimingStatus`; the analyzer rejects missing samples. A CPU arm has no GPU cull stage. Timed runs intentionally have no correctness readbacks; the analyzer requires a matching **separate** validation JSON. The known visible count is a frozen oracle count, not a timed GPU telemetry measurement. Ten validation frames compare exact sorted sets, counts, non-black pixels and RGBA hashes.

CPU timings describe main-thread API costs, not hidden render-thread work. GPU ranges omit CPU-arm upload copies. Update intervals are scheduling diagnostics, not completion latency. Do not compare CPU milliseconds to GPU milliseconds or add them into a fabricated critical path. The analyzer reports CPU resource relief only, with process-paired bootstrap intervals; it does not declare an architecture crossover.

## Tests

The repository xUnit project links the exact `BenchmarkModel.cs` used by Unity and tests deterministic generation, boundary predicates, full-scan/list output, calibration, parsing and set comparison. `python -m unittest discover -s tools -p test_crossover.py` tests schema rejection, paired analysis and balanced scheduling. Both run in existing CPU CI. Unity shader compilation and GPU set/image/timing checks additionally require a local player build; the hosted CPU CI cannot establish those hardware properties.
