# Experiment tracker

| Stage | Purpose | Status |
|---|---|---|
| Model and analysis | Determinism, predicate, parser, sets, schema, paired analysis | 218 C# and 24 Python tests passed; hosted CI pending |
| Player build | Standalone Development Mono D3D12 | Built; diagnostics in local build log |
| Smoke v1 | 100k / 25%, correctness | Stopped: sets equal but black output; no timings collected |
| Corrected smoke | Fix logical far-depth clear handling; new source identity | 100k/25%: 10/10 sets/counts and non-black images passed |
| Full correctness matrix | All 18 conditions, 10 frames each | Stopped before remaining 17; timing feasibility first |
| Timing pilot | Delayed GPU queries, pacing and drift | STOP: 121 submitted measured frames, zero resolved GPU ranges; background GPU load also present |
| Primary measurements | 216 independent processes, balanced order | 0 complete; not started |
| Report | Evidence-backed costs/crossover or explicit stop | [Stop report](../docs/results/GPU_RESIDENT_CROSSOVER_2026-09-19.md); no performance/crossover claim |
