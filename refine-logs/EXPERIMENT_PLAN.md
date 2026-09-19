# GPU-resident crossover experiment plan

Date: 2026-09-19. Authoritative preregistration: [Unity benchmark protocol](../unity/GpuDrivenCrowdBenchmark/PROTOCOL.md).

## Claim map

| Question | Minimum evidence | Anti-claim |
|---|---|---|
| Does moving culling/count decisions to GPU change CPU and GPU resource costs? | Equal full visible sets and images, separate CPU/GPU timing, repeated fresh balanced processes | A faster submission call alone proves a faster architecture |
| Is there a measured architecture crossover? | Comparable critical-path metric, process-level uncertainty excluding parity, validity gates | CPU timestamp vs GPU timestamp ratio establishes latency |

## Mandatory blocks and execution order

1. Shared deterministic contract, CPU oracle and parser/analysis tests.
2. Standalone D3D12 correctness: 18 conditions × 10 selected frames; full visible-ID sets and RGBA output.
3. Timing feasibility pilot with 300 warmup and 1000 measured frames; delayed GPU queries and pacing/drift audit. Stop if instrumentation is unavailable or distorts execution.
4. Conditional collection: 18 conditions × 6 balanced pairs × 2 arms = 216 fresh processes. Freeze source and retain every raw outcome.
5. Process-level bootstrap, plots of eligible resource costs, report limitations/decision. No end-to-end winner without an eligible critical-path measure.

## Budget and risks

Budget 1–3 local GPU-hours if timing gates pass; no remote compute. Calibration and pixel readback are outside timing. Main risks are Unity GPU query availability, driver presentation pacing, same-desktop background load, append-order rendering effects and scalar-baseline generalizability. The protocol states stop conditions before timings are seen. Burst/Jobs tuning, application-specific spatial filtering and new GPU algorithms are excluded from this first baseline.

This is an engineering workload study, not an ML novelty claim; paper-specific backbone/dataset/LLM ablations are inapplicable.
