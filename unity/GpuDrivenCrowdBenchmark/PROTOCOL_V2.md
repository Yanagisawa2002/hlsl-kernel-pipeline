# Protocol amendment 2 — measurement infrastructure checkpoint

Frozen before v2 runs. v1 produced **zero valid GPU timing estimates** and remains archived unchanged. This amendment changes instrumentation and launch-mode diagnostics only; population, predicate, trajectory, CPU algorithm, GPU culling/indirect rendering, geometry, depth fairness rule and output resolution remain unchanged.

## Diagnostic sequence

1. Test three stable legacy CustomSampler/Recorder markers: Crossover/Range, Crossover/Cull, Crossover/Draw. Read once each Update; documentation says availability frame minus three. A deterministic nonperiodic 1–4 repetitions code over 96 submission frames plus 16 drain frames tests block-count attribution rather than assuming a one-to-one match from constant samples.
2. Same Development D3D12 binary/workload, normal hidden standalone versus hidden batchmode. Keep both outcomes. If stable legacy fails, test modern SampleGPU ProfilerMarker + GpuRecorder ProfilerRecorder in the same A/B. Do not average APIs. Do not enable broad binary profiling or add synchronization as an undocumented fallback.
3. API-availability diagnostics can run on an active desktop, with before/after load recorded, solely to establish whether samples exist and test attribution. They are explicitly **not timing-quality or performance pilots**. All pilots require three pre-run NVIDIA snapshots <=5%, as does formal timing; blocked attempts are retained, with no automatic retry.
4. Adopt an API/launch mode only if all 96 submitted count codes yield positive samples in every range, agree with the documented mapping, and no frame drop/ambiguous association is found. A best-fit alternative lag is diagnosis, not permission to remap silently.

## Comparable boundary and pacing

Proposed primary metric (conditional on pilot validation): batchCompletionMsPerFrame. Record first measured CPU timestamp; submit 1000 frames normally; insert one final GraphicsFence after final measured GPU draw, then poll passed in subsequent Updates. Require supportsGraphicsFence, no blocking wait, no new benchmark submissions while draining. This is amortized batch throughput/completion, not per-frame latency. Warmup completion uses a separate once-per-batch fence before measurement starts, so outstanding warmup work cannot pollute the boundary.

Record raw Stopwatch ticks/frequency at batch start, last measured submission, last false/first true fence poll and the enclosing update interval. The observation error bound is the gap from last false poll (or final submission if first poll already passes) to first true; divide by 1000. CPU timing calls are not GPU execution timestamps.

Compute p10/median/p90/p95/CV of measured update intervals and detect concentration within ±3% of display intervals across 30–360 Hz (including 60/120/144), plus tight unexplained low-variance clusters. Flag a pacing stop when >=80% cluster around a common interval, or median >2ms and CV<0.02. Settings alone do not establish unpaced behavior. This conservative diagnostic may flag a truly stable workload; investigate, do not override from timing alone.

## Authorized extent and stops

No 216-process matrix, no merge, no README conclusions. After diagnostic selection, rerun 100k/25% correctness on final binary, then one quiet CPU pilot and one quiet GPU pilot (300+1000), then three additional fresh pairs in CPU/GPU, GPU/CPU, CPU/GPU order. Three pairs necessarily have 2:1 first-arm balance; record that limitation. No crossover claim or formal CI from three pairs.

All pilot gates: complete correctly mapped GPU stages, final fence, <=5% pre-run GPU load, zero measured Gen0 collections, <=15% first/last-quarter drift, pacing pass, matching schema/identity. Any failed gate stops later steps. Only once the timing code is stable and pilot gates pass, finish the remaining 17 correctness cells; formal timing remains blocked until 18/18 pass and separate explicit authorization arrives.

Sources: [legacy GPU count delay](https://docs.unity3d.com/cn/6000.0/ScriptReference/Profiling.Recorder-gpuSampleBlockCount.html), [GPU ProfilerRecorder option](https://docs.unity3d.com/cn/6000.0/ScriptReference/Unity.Profiling.ProfilerRecorderOptions.GpuRecorder.html). API flags are not proof of working samples.

## Bounded follow-up after first diagnostic A/B (before any valid timing)

Both APIs in both launch modes returned zero GPU samples with valid markers/recorders; GPU profiler area reported disabled. One hypothesis-driven legacy A/B explicitly enables that area using SetAreaEnabled, with before/after flags persisted. It does not enable broad binary profiling or wait for the GPU. If this also returns zero, stop API experimentation and record the unresolved backend/configuration issue. Preserve the initial four probes in their original directory; use a new directory/binary identity for this check.
