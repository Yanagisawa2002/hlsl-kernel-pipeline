# Controlled CPU-driven / GPU-resident Crowd benchmark

**Protocol-v3 checkpoint: native timestamp IDs/fences pass short GPU diagnostics; timed CPU/GPU integration remains pending and PR #12 stays Draft.** Read the [v3 findings](../../docs/results/GPU_TIMING_V3_2026-09-19.md) and [PROTOCOL_V3.md](PROTOCOL_V3.md). Read [PROTOCOL_V2.md](PROTOCOL_V2.md) and the [repair stop report](../../docs/results/GPU_TIMING_REPAIR_V2_2026-09-19.md). [PROTOCOL.md](PROTOCOL.md) preserves the original v1 plan. This dedicated workload leaves the live sample and the September 16 complete-task benchmark unchanged. It measures a managed all-agent CPU-single baseline and the append/count-copy GPU path with the same deterministic population, trajectory, quad shader and offscreen target.

## Build (Windows, Unity 6000.3.13f1)

From the repository root, with Python and the Unity Windows standalone module installed:

```powershell
python tools/prepare_crossover_project.py --project D:/CodexValidation/crossover-project
$env:CROSSOVER_PLAYER_PATH = 'D:/CodexValidation/crossover-player/Crossover.exe'
& 'C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Unity.exe' -batchmode -quit -projectPath D:/CodexValidation/crossover-project -executeMethod HlslPerf.Crossover.BuildCrossover.Build -logFile D:/CodexValidation/crossover-build.log
```

Check build exit, log and `Crossover.exe.build.json`. The generated project/player stay outside the repository. The build embeds a hash of the benchmark C#/shader sources; source changes require rebuilding and a new evidence directory. GPU Recorder requires the Development player; no attached profiler, deep profiling or script debugger is enabled.

## Diagnostic and gated stages

Build once, then compare the same binary in normal hidden standalone and hidden batchmode:

```powershell
python tools/run_timing_diagnostic.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-v2-legacy --api legacy
```

If stable legacy fails, repeat with `--api profiler-recorder` in a distinct output directory. The diagnostic uses three stable markers and a deterministic 1-4 block-count sequence over 96 frames, plus 16 drain frames. Its raw observations allow checking the documented three-frame mapping. The two API results are never averaged. These availability probes are not clean timing-quality pilots, and record background load. A bounded GPU-area check is documented in protocol v2; do not retry arbitrary configurations until something looks favorable.

Once a final-binary diagnostic has **passed**, regenerate calibration and correctness for the first cell:

```powershell
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-v2 --stage calibrate --agents 100000 --densities 0.25
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-v2 --stage validation --agents 100000 --densities 0.25
```

The following are gated examples, **not authorization to bypass the current failed diagnostic**:

```powershell
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-v2 --stage pilot --agents 100000 --densities 0.25 --timing-diagnostic D:/CodexValidation/crossover-v2-legacy/legacy-normal.json --launch-mode normal
python tools/run_crossover.py --player D:/CodexValidation/crossover-player/Crossover.exe --output D:/CodexValidation/crossover-v2 --stage pilot-pairs --agents 100000 --densities 0.25 --timing-diagnostic D:/CodexValidation/crossover-v2-legacy/legacy-normal.json --launch-mode normal
```

`pilot` launches one CPU then one GPU process, stopping immediately if either fails. `pilot-pairs` requires those two passing pilots and launches exactly three fresh pairs, CPU/GPU, GPU/CPU, CPU/GPU. All timing pilots require the same three <=5% pre-run GPU-utilization samples as formal measurement, plus correctness/identity, complete GPU mapping, final fence, pacing, GC and drift gates. Blocked receipts are retained; no automatic retries. Three pairs are a stability check, not crossover evidence.

The formal `measure` stage is disabled at this checkpoint. After timing is validated, all remaining correctness cells must pass before a separately authorized formal experiment. The v1 analyzer remains for archived schema-1 data; it intentionally rejects schema 2 rather than pooling versions. `check_crossover_timing.py` supplies v2 diagnostic, batch-boundary and pacing validators.

## Measurement boundary

The candidate comparable metric is `batchCompletionMsPerFrame`, first measured CPU work through the final measured GPU fence observation, amortized across all frames. A separate warmup fence prevents queued warmup work entering the measured batch. No per-frame wait, count readback or forced GPU completion is introduced. After the final buffer is submitted, only fence/timing polling continues. Raw ticks/frequency, submission, last-false/first-true observation and quantization bound are recorded. This is throughput/completion, not individual-frame latency; it remains hardware-unvalidated in this delivery.

## Metrics and schema

Raw run JSON schema 2 (`protocolVersion: 2`) contains source/calibration identities, runtime adapter/API/driver, process/pair IDs, options, full per-frame samples and validation checks. Each measured sample has CPU fused cull/list, CPU upload, submission and total durations, plus GPU cull, draw and command-range durations. Stable markers are reused during warmup and measurement. Samples map to `Time.frameCount - 3`, with submission/availability IDs checked and stored; any missing/ambiguous sample fails closed. GPU cull includes counter reset/copy. CPU list work is fused with the predicate; it is not falsely assigned a separate duration.

`-1` marks unavailable GPU timing, with `gpuTimingStatus`; the analyzer rejects missing samples. A CPU arm has no GPU cull stage. Timed runs intentionally have no correctness readbacks; the analyzer requires a matching **separate** validation JSON. The known visible count is a frozen oracle count, not a timed GPU telemetry measurement. Ten validation frames compare exact sorted sets, counts, non-black pixels and RGBA hashes.

CPU timings describe main-thread API costs, not hidden render-thread work. GPU ranges omit CPU-arm upload copies. Update intervals are scheduling diagnostics, not completion latency. Do not compare CPU milliseconds to GPU milliseconds or add them into a fabricated critical path. The v1 analyzer reports only CPU resource relief. No formal v2 statistics or crossover are published at this checkpoint.

## Tests

The repository xUnit project links the exact `BenchmarkModel.cs` used by Unity and tests deterministic generation, boundary predicates, full-scan/list output, calibration, parsing and set comparison. `python -m unittest discover -s tools -p test_crossover.py` tests schema rejection, paired analysis and balanced scheduling. Both run in existing CPU CI. Unity shader compilation and GPU set/image/timing checks additionally require a local player build; the hosted CPU CI cannot establish those hardware properties.

## Native diagnostic (Windows D3D12 only)

Uses installed Unity PluginAPI headers and MSVC. DLL binaries stay outside the repository. Normal builds explicitly disable Graphics Jobs; D3D11 is an optional diagnostic override, not a benchmark change.

```powershell
python tools/build_crossover_native.py --unity "C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor" --output D:/CodexValidation/crossover-native
python tools/prepare_crossover_project.py --project D:/CodexValidation/crossover-native-project --native-plugin D:/CodexValidation/crossover-native/CrossoverTimestamp.dll
$env:CROSSOVER_PLAYER_PATH = 'D:/CodexValidation/crossover-native-player/Crossover.exe'
$env:CROSSOVER_AUTOCONNECT = '0'
& 'C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Unity.exe' -batchmode -quit -projectPath D:/CodexValidation/crossover-native-project -executeMethod HlslPerf.Crossover.BuildCrossover.Build -logFile D:/CodexValidation/crossover-native-build.log
python tools/run_native_timing_diagnostic.py --player D:/CodexValidation/crossover-native-player/Crossover.exe --output D:/CodexValidation/crossover-native-probe
```

Check the build receipt/process before invoking the runner. It runs two fresh 96-submission processes and validates explicit IDs, fence completion, timestamp conversion and source/DLL hashes. This mode requires no calibration and does not enable the profiler. It is not a CPU/GPU timing pilot.

Legacy activation diagnostics additionally support `--startup-profiler` and `--graphics-api d3d11` on `run_timing_diagnostic.py`. Set `CROSSOVER_AUTOCONNECT=1` only for a separate connection-diagnostic build. `ProfileCapture.Autoconnect` takes `CROSSOVER_PLAYER_PATH`, `CROSSOVER_PLAYER_ARGS` and `CROSSOVER_PROFILE_RAW`, configures Editor GPU profiling before starting its child, and saves capture on completion. `ProfileCapture.InspectMany` takes semicolon-separated `CROSSOVER_PROFILE_INPUTS` and exports GPU hierarchy CSVs using Unity Editor APIs. These internal hierarchy columns are Unity-version-dependent; raw files must be retained. No GUI acceptance is inferred from the controller alone.
