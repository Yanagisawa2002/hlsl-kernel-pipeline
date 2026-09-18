# Preregistered crossover protocol v1 — 2026-09-19

Question: when does GPU-resident visibility plus rendering become preferable to an all-agent CPU visibility path? No preferred winner. This is separate from the September 16 CPU-output benchmark and does not repeat RenderDoc architecture validation.

## Contract and anti-claims

Both arms use xorshift32 seed 69501203, immutable 28-byte agents, world half-extents (120,68), sizes [0.06,0.16), identical colors, six-vertex quads and the same rectangular padded visibility predicate. Each frame scans all N agents. No cached eligible subset, simulation, spatial index, sorting in the timed GPU path, Burst or Jobs. CPU-single is a managed scalar baseline, not a universal CPU baseline.

Both render to a linear RGBA8 1280×720 offscreen texture, D32 depth, no MSAA, no camera or overlay. Unlike the live demo, both use unique agent-ID depth and depth writes so overlapping quads have order-independent colors. This is a documented rendering-contract change shared by both arms, not a change to the archived visual demo. Depth rejection can still make draw work depend on append order; report this limitation.

## Frozen matrix and calibration

N = 100000, 250000, 500000, 1000000, 2000000, 4000000. Target mean visibility = 0.05, 0.25, 0.75. Preserve failed/memory-limited cells.

Population prefixes are identical for the same seed. A separate calibration process derives a view scale from a 65536-bin histogram of minimum admission scales over 32 evenly spaced trajectory frames. Then an all-agent CPU oracle computes actual counts for **all 1000 measured frames**. Save the view values and counts in JSON; both arms load identical bytes. Report actual mean, not the target as if exact. This oracle is never a CPU arm's visibility shortcut.

Trajectory: frame-index time `f/60`, phase `time*0.18`, center `(cos(phase)*45, sin(phase*0.73)*45*0.55)`. Calibration stores float view values, eliminating cross-process trigonometry differences. Warmup replays frames 0..299; measurement restarts at frame 0.

## Lifecycle and order

Development Mono standalone player, Windows D3D12, script debugging/deep profiling disabled. `-batchmode -force-d3d12`, **never** `-nographics`. VSync 0, targetFrameRate -1, runInBackground true. No onscreen FPS metric.

Setup/generation/allocations/export are excluded. 300 warmup + 1000 measured frames. Six pairs per condition (12 fresh, serial processes), alternating CPU/GPU and GPU/CPU, giving exact order balance and six independent values per arm. Interleave conditions across rounds, reversing condition order on alternating rounds. Immutable source identity and player hash bind the run. Freeze code before launching the primary matrix; pilots have distinct IDs and are not pooled.

## Correctness gate

Separate validation process for every condition, same executable/calibration/API. Ten frames evenly spanning 0..999 including both endpoints. Exact count and sorted full visible-ID equality (detect duplicates as well as omissions), exact RGBA8 pixel hash equality, non-black image check. Synchronous diagnostic readbacks occur only in this mode. Any mismatch stops the experiment. Timed GPU uses no readback, including telemetry; reported visibility is the validated deterministic oracle, clearly labeled. Timed CPU also checks its actual count against the oracle.

## Metrics and interpretation

- CPU: fused cull/list wall time (cannot separately attribute predicate and list stores without changing the baseline), visible-index `SetData` CPU duration, command construction/enqueue duration, total benchmark CPU call duration. These include any implicit driver backpressure but exclude asynchronous render-thread work.
- GPU: delayed `CustomSampler` GPU Recorder queries for each unique measured frame; cull range includes reset/dispatch/counter copy, draw range, total command range. CPU arm total GPU range covers drawing, **not** upload copies enqueued by SetData. Consequently GPU range alone cannot rank entire architectures. No blocking query fallback.
- Update intervals and Gen0 collection count are diagnostic. No per-frame GC/forced collection. Initial allocation stays out of timing.
- Unsupported/missing GPU timings are unavailable, encoded -1 with status in raw Unity JSON and null in summaries, never zero. Require all measured marker samples, including final drain; a 120-frame nonblocking timeout stops the run.

The primary architecture crossover is **not estimable from CPU wall milliseconds versus GPU timestamp milliseconds**. Until a trustworthy completion/critical-path metric is available, report separate CPU and GPU resource costs and explicitly withhold end-to-end winner labels. An Update-to-Update ratio is not a substitute. A future primary metric needs a new protocol version before primary collection.

If a validated comparable architecture metric becomes available in that version: GPU-favorable requires a paired 95% interval entirely above 1 for CPU/GPU cost, correctness and all validity gates. Intervals spanning 1 are inconclusive. Only contiguous measured transitions bracket a crossover; isolated wins are condition-specific. Never add CPU and GPU times as though they cannot overlap.

## Statistics, drift and stops

Process means/medians, not individual frames, are independent units. For comparable **same-resource** metrics, bootstrap six matched launch-pair process means (10000 draws, fixed analysis seed 69501203); report ratio of means with paired 95% percentile interval. Label CPU resource ratios as CPU relief, never end-to-end speedup. No architecture ratio plot without an eligible metric.

Log NVIDIA adapter/driver/temperature/utilization snapshots before and after each process. The primary runner requires three pre-run samples at <=5% GPU utilization; temperature change >10°C or nonzero process failure invalidates the run and stops collection. Within-process first/last quarter CPU/GPU mean drift >15% flags investigation (do not discard selected frames). Nonzero Gen0 collections flag review. Preserve all outcomes; no silent retries or selective sample removal. Ambient/background attribution remains imperfect on a shared desktop.

Stops: set/image mismatch, unavailable/unresolved GPU timing, unexplained pacing, drift, non-independent processes, unequal work. Fix/version methodology before interpreting. It is acceptable to stop with a validated infrastructure and a blocked experiment report. No README performance conclusions until controlled valid measurements exist.

## Stages and budget

1. Model/parser/analysis tests and full repository CI.
2. Player build; calibration and correctness smoke, then all 18 conditions (180 frame checks).
3. CPU/GPU pilot with full warmup/sampling; timing/pacing/drift validity gate.
4. Only if gate passes: 216 fresh measurement processes (18×6×2), raw JSON, summary/CSV/plots and scoped report.

Expected primary duration is workload-dependent; budget 1–3 local GPU-hours including preparation. No remote server lifecycle changes. Stop before primary collection if diagnostics invalidate timing. GPU Recorder markers add instrumentation overhead shared by arms; quantify before asserting latency.

References: [CustomSampler GPU data](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Profiling.CustomSampler.Create.html), [CommandBuffer sampling](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.CommandBuffer.BeginSample.html), [delayed GPU Recorder results](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Profiling.Recorder-gpuElapsedNanoseconds.html).
