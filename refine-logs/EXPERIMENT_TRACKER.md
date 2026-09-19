# Experiment tracker

| Stage | Purpose | Status |
|---|---|---|
| Model and analysis | Determinism, predicate, parser, sets, schema, paired analysis | v1: 218 C# and 24 Python tests passed; hosted CI passed at b119e01 |
| Player build | Standalone Development Mono D3D12 | Built; diagnostics in local build log |
| Smoke v1 | 100k / 25%, correctness | Stopped: sets equal but black output; no timings collected |
| Corrected smoke | Fix logical far-depth clear handling; new source identity | 100k/25%: 10/10 sets/counts and non-black images passed |
| Full correctness matrix | All 18 conditions, 10 frames each | Stopped before remaining 17; timing feasibility first |
| Timing pilot | Delayed GPU queries, pacing and drift | STOP: 121 submitted measured frames, zero resolved GPU ranges; background GPU load also present |
| Primary measurements | 216 independent processes, balanced order | 0 complete; not started |
| Report | Evidence-backed costs/crossover or explicit stop | [Stop report](../docs/results/GPU_RESIDENT_CROSSOVER_2026-09-19.md); no performance/crossover claim |

## Protocol-v2 repair checkpoint

- Stable legacy and modern marker APIs: both normal/batch A/Bs completed with zero GPU samples. Explicit GPU-area legacy A/B also returned zero. Six retained availability probes; no API selected.
- Nonblocking final-fence completion boundary implemented and built, but hardware timing/pacing validation remains unavailable.
- Final-binary 100k/25% correctness: 10/10 sets/counts/images passed. Remaining 17 cells deferred while timing is unresolved.
- Local validation: 220 C# and 28 Python CI-suite tests passed; final standalone build succeeded. Current hosted CI/head tracked in Draft PR #12.
- No v2 CPU/GPU pilot, no three-pair pilot and no formal matrix. Not infrastructure-merge-ready.
- [Second stop report](../docs/results/GPU_TIMING_REPAIR_V2_2026-09-19.md) and [protocol v2](../unity/GpuDrivenCrowdBenchmark/PROTOCOL_V2.md).

## Protocol-v3 diagnostic checkpoint

- Graphics Jobs explicitly off; runtime normal MultiThreaded, batch SingleThreaded. D3D11 stays diagnostic-only.
- Startup D3D11/D3D12 raw hierarchy: positive GPU timing despite zero Recorder. Autoconnect Recorder: 96 nonzero observations, but only 68/96 match lag 3.
- Native D3D12 explicit-ID/frame-fence diagnostic: final normal and batch each pass 96/96 with profiler off; 32-slot reuse exercised. Not integrated into timed CPU/GPU runner yet.
- Final binary correctness: 100k/25%, 10/10 passed. No benchmark pilots or full matrix.
- [V3 report](../docs/results/GPU_TIMING_V3_2026-09-19.md). PR remains Draft, not infrastructure-merge-ready.
