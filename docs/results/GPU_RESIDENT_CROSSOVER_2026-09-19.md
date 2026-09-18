# GPU-resident crossover — infrastructure and timing stop report

**Status: STOP before the primary performance matrix.** The all-agent CPU/GPU paths agree in the completed correctness smoke, but delayed GPU timing produced no valid samples. No architecture crossover has been measured. This is not evidence that no crossover exists in the planned range.

## 1. Research question

Under what workload conditions does GPU-resident culling plus indirect rendering become preferable to an all-agent CPU visibility path? The [preregistered protocol](../../unity/GpuDrivenCrowdBenchmark/PROTOCOL.md) defines fairness, sampling, independent units, uncertainty and stop conditions. The September 16 complete CPU-output benchmark and its CPU12 conclusion are unchanged. Existing RenderDoc architecture validation was not repeated.

## 2. Workload contract

Immutable seeded 28-byte agents; six-vertex colored quads; world half-extents (120,68); rectangular visibility `abs(position - viewCenter) <= viewHalfExtent + size`. Both arms scan every agent every frame. No simulation, static eligible-subset cache, sorting in timed runs or differing shader appearance.

Seed 69501203 uses a specified xorshift32 generator. A separate process calibrates view extents using the fixed population, then stores all 1000 frame-index-derived views/counts. Both arms load the same calibration bytes. The complete trajectory and calibration contract are in the protocol. This benchmark's deterministic generator is independent of the historical visual demo's `System.Random` sequence.

## 3. CPU/GPU path definitions

**CPU-single:** a managed scalar loop tests all N agents, immediately appends each visible ID into a preallocated array, uploads the visible prefix with `ComputeBuffer.SetData`, then enqueues a six-vertex `DrawProcedural` with the CPU-derived count. Predicate and list preparation are fused and measured together. No Jobs/Burst/multithreading claim is made.

**GPU-resident:** reset append counter, dispatch unchanged `CullAgents` predicate, copy append count into indirect argument offset 4, issue procedural indirect draw using the visible-ID buffer. The CPU does not decide visibility/count, and the timed GPU path performs no readback at all. Counter-copy and indirect draw ordering are maintained in one command buffer.

## 4. Fairness controls

Same source/calibration hash, seed/population, view schedule, geometry, material, resolution and frame schedule. Both render offscreen to linear RGBA8 1280×720 with D32 depth and no MSAA. VSync is 0, target frame rate -1, no camera/overlay or displayed FPS metric.

The visual demo used depth-disabled overlapping quads whose colors depended on append order. The **separate benchmark shader** assigns each agent a unique exactly spaced depth, uses depth writes and a common comparison rule. This makes final colors independent of visible-ID order without GPU sorting. It changes the rendering contract for both arms equally; early depth rejection and GPU memory locality can still depend on order and limit generalization.

## 5. Correctness validation

Separate standalone validation processes perform diagnostic readbacks outside timing. Ten frames (0,111,222,333,444,555,666,777,888,999) compare CPU/GPU counts, sorted complete ID sets, non-black render output and exact RGBA hashes.

The first 100k/25% smoke stopped: sets/counts matched (25,362 at frame 0), but both images were black. Hash equality alone would have missed this defect. The cause was passing reversed hardware depth to an API that converts logical far depth itself. Changing the shared clear value to logical far depth 1 fixed it. The failed output was retained under a different source identity.

The corrected smoke passed **10/10 count/set checks and 10/10 non-black image comparisons**. This validates only **one of 18 planned conditions**, not the complete matrix. See [raw checks](../evidence/gpu-resident-crossover-20260919/smoke-v2/validation-100000-0.25.json).

## 6. Hardware/software identity

| Field | Observed value |
|---|---|
| GPU | NVIDIA GeForce RTX 4090 |
| Driver | Player log: 32.0.15.9186; NVIDIA runtime snapshot: 591.86 |
| CPU | Intel Core Ultra 7 265K |
| OS | Windows 11, build 10.0.26200 |
| API | Direct3D12, feature level 12.2 |
| Unity | 6000.3.13f1 |
| Build | Standalone Development Mono; no script debugger/deep profiling |
| Target | 1280×720 offscreen, RGBA8 linear, D32, no MSAA |
| Pacing requests | VSync 0, targetFrameRate -1, batchmode with graphics |
| Source identity | `f7365d1009a51fa8b9bcde8cf7955fafbbf7eeee0cb92dab443630b8fe2eb785` |

The runtime `driver` JSON extractor returned unavailable; the driver above is taken from the **actual player log and launch snapshot**, not hardcoded or confused with API version. Source and managed-assembly identities are in the evidence. The GPU Recorder capability flag and Frame Timing Stats feature both reported true; those flags did not establish working marker measurements.

## 7. Benchmark protocol

Planned counts: 100k, 250k, 500k, 1M, 2M, 4M. Planned visibility targets: 5%, 25%, 75%, calibrated per population. 300 warmup plus 1000 measured frames, six balanced independent pairs per cell (216 fresh processes total), alternating launch order and interleaving cells across rounds.

Process means, not frame observations, are independent units. The analyzer supports paired bootstrap uncertainty for same-resource CPU relief; an architecture win would require an eligible critical-path metric with its interval excluding parity, correctness and clean timing. Update-to-Update scheduling intervals are not such a metric. CPU wall time is never divided by GPU timestamp time as an architecture result.

Before spending time on the remaining 17 correctness conditions, one timing-feasibility pilot was run on the passing smoke cell. This ordering deviation only moved feasibility ahead of the full matrix; no performance sample selection or winner criterion was changed.

## 8. Results

Derived from the [diagnostic summary](../evidence/gpu-resident-crossover-20260919/diagnostic-summary.json):

| Condition/stage | Completed evidence | Outcome |
|---|---|---|
| 100k, target 25%, calibration | 1000 deterministic views/counts | Actual mean visible ratio 25.0007% |
| Corrected correctness | 1 fresh validation process, 10 selected frames | 10/10 sets/counts and 10/10 non-black images equal |
| CPU timing-feasibility pilot | 1 attempted process; 300 warmup, 121 submitted measured frames | Exit 2; zero resolved GPU range samples |
| GPU timing-feasibility pilot | 0 processes | Not launched after the stop |
| Formal matrix | 0/216 completed processes; 0 per condition per arm | Not started |
| Other 17 conditions, including 4M | No correctness/performance runs | Not tested; no memory-limit claim |
| CPU/GPU mean costs, ratio, interval | Unavailable | Incomplete pilot is excluded |

Raw arrays were allocated for 1000 frames; unexecuted slots are not observations. No partial means, benchmark summary plots or ratio plots were produced. The analysis tool refuses pilot files and incomplete results. Plot A/C support exists for future valid resource-cost data; Plot B is ineligible until a comparable architecture metric exists.

## 9. Crossover interpretation

**Not determined.** There is neither a measured crossover region nor a defensible “no crossover observed in the tested range” result, because the primary timing range was never tested. Confidence intervals and architecture ratios are unavailable, not zero or near parity.

## 10. Profiling explanation

The pilot used unique per-frame `CustomSampler` markers with GPU collection enabled for command range, cull and draw, polled asynchronously. It never called a GPU completion wait or readback for timing. After the first measured query remained unresolved for 120 frames, the process wrote an error result and exited. All GPU range values remained unavailable despite `supportsGpuRecorder == true`.

This diagnoses **failure of the current instrumentation configuration**, not proof that Unity/D3D12 can never provide GPU timings. No dispatch CPU duration was substituted, and no forced synchronization fallback was added. A follow-up must establish a working nonblocking GPU timestamp/profiler configuration and characterize its overhead before rerunning a versioned protocol. GPU range excludes CPU-arm upload copies, so even working markers alone would not establish critical-path latency.

## 11. Limitations

- Correctness covers one cell, not all 18; the corrected renderer has not passed 4M validation.
- No valid repeated timing processes exist; no performance distribution, scaling or latency claim is supported.
- The pilot was diagnostic and not quiet: NVIDIA snapshots were 33% utilization before and 15% after, at 45–46°C. Primary launch policy would reject that pre-run load. Background attribution and presentation pacing remain unvalidated.
- The managed scalar CPU baseline is deliberately straightforward; it is not representative of every optimized CPU/Jobs/Burst implementation.
- Development build, per-frame marker registration and Unity/driver scheduling require their own overhead audit. GPU upload, CPU/render-thread overlap and end-to-end completion remain outside a defensible architecture metric.
- Driver extraction into JSON is incomplete; preserved runtime logs/snapshots provide identity instead. No current performance result relies on that missing field.

## 12. Engineering decision and delivery

**Do not select CPU or GPU based on this pilot.** Preserve the existing architecture evidence and the earlier CPU12 benchmark conclusion. Keep this work as a **Draft infrastructure PR** until delayed GPU timing, pacing and the full correctness gate are resolved. A useful next change is instrumentation repair/verification, not tuning either algorithm to force a favorable result.

Branch: `codex/gpu-resident-crossover-benchmark-20260918`, based on main `916c300`. The top-level README has no performance update. The benchmark runtime, builder, protocol, runner, analyzer, tests and diagnostic evidence are separate from existing demos.

Local validation: Unity standalone player build succeeded; managed Release solution build passed with zero warnings/errors; **218 C# tests, 24 Python tests, 12 CPU plan checks and 28 SDK shader-entry compilations passed**. Existing hosted CPU/compile CI is run on the Draft PR; its result is recorded in the PR and final delivery. Hosted CI does not validate Unity hardware timing.

Raw local evidence: `D:/CodexValidation/hlsl-crossover-smoke-20260919/` and `D:/CodexValidation/hlsl-crossover-smoke-v2-20260919/`; [committed package](../evidence/gpu-resident-crossover-20260919/README.md) includes sanitization and SHA-256 provenance. Large generated player/project/Library files remain local.
