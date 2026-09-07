# HLSL focused-cost confirmation

The single fixed Radix ballot candidate passes correctness and has a lower point estimate, but **no comparison passes the complete predeclared stability contract**. Keep the candidate opt-in and make no default-policy changes. Scan has no new candidate. Sampling is complete; no retries were used to obtain a favorable result.

Only Radix 1 Mi uint32 pairs, uniform, one resident slot, and Scan 8 Mi full-uint32 exclusive modulo addition, one slot were measured. Five independent processes per cell use 12 balanced paired blocks and 18 complete operations per batch. Diagnostic pass markers and software counters are excluded from confirmation.

| Numerator / denominator | GPU time ratio [95% CI] | Decision |
|---|---:|---|
| internal-radix-8 / internal-radix-8-ballot | 1.675849 [1.655716, 1.696227] | Inconclusive |
| internal-radix-8 / amd-parallel-sort | 2.433827 [2.398934, 2.469227] | Inconclusive |
| internal-radix-8-ballot / amd-parallel-sort | 1.452295 [1.425471, 1.479623] | Inconclusive |
| internal-scan-single / gps-reduce-then-scan | 2.497072 [2.114331, 2.949097] | Inconclusive |

Ratios above one favor the denominator. Intervals use five process log ratios (Student t, df=4); within-process operations are correlated. All p95 ratios and lower CI bounds exceed 1.01, but CV violations invalidate every acceptance decision. Scan also violates drift. These are individual descriptive comparisons without multiplicity correction.

For original Radix / ballot, original CV exceeds 5% in p1/p3/p5; candidate CV exceeds 5% in p1/p2/p3/p5. Max CV is 6.6812%, max drift 4.8197%, and minimum p95 ratio 1.673458. For Scan / RTS, p2/p4/p5 violate CV, and internal Scan p2/p4 drift is 54.8188%/44.5057%. Do not discard these processes or infer equivalence from the failed decision.

The original Radix mean is 0.727559 ms, ballot 0.434182 ms, and AMD 0.298857 ms. These are arithmetic process means, distinct from the paired geometric time ratios. The fixed candidate reduces the point-estimated gap, but still has a 1.452295 candidate/AMD ratio and fails the stability gate. Scan/RTS means are 0.190910/0.072956 ms. Earlier published and current process sets are not pooled.

Both diagnostic epochs put approximately 69% of internal Radix time in scatter. Its source performs 534,773,760 shared-array equality comparisons per full operation. The sole candidate uses 288 shared bytes of valid bit planes and population counts for stable rank. Group128, two records/thread, eight-bit digits, all 29 passes, required input/payload loads and output conversion remain. The scatter newly specifies WaveSize32; the evidence applies to the entire fixed candidate, not rank logic alone. SV_GroupIndex/32 versus WaveGetLaneIndex assumes the current device lane grouping. Ninety native cases verify R9700, not cross-device portability. The macro defaults to zero.

Scan's prior algorithm gap accounts for 92.24% of its complete-operation gap, while internal CPU recording is lower. Its 64 MiB main-array logical read/write count versus RTS 96 MiB does not establish DRAM traffic or bandwidth. Scalar strided accesses, per-thread prefixes, serial wave-total scan, persistent task claims and coherent lookback are plausible contributors. Instrumented snapshots observe retries, but change timing and cannot establish the baseline contribution of each mechanism. No isolated fixed repair was selected.

The first diagnostic used one operation/submission and had substantial timing differences from prior formal batches. A retained second epoch aligns to 18 operations but still uses per-pass markers, so neither epoch is formal performance evidence. No hardware cache/DRAM/occupancy/clock counters were collected. A first candidate build test failed because it compared empty byte arrays by reference; a content-hash assertion fixed the test without changing the algorithm. All failures are retained.

Validation: Release build without warnings/errors; 105 Core tests and 3 protocol tests; 90 candidate cases / 360 complete poisoned output checks; 10 formal processes / 25 arm-processes / 160 complete output checks, all passing; 600 measured batches / 10,800 complete operations plus 1,200 warmup operations. Six diagnostic processes, 24 arm/process pass summaries and 18 software-counter snapshots are audited separately. Source files, compiled DXIL identity, actual binaries, device, declaration, PID, command, order, oracle hashes and timestamp boundaries are checked.

Measurement source: `3874eb0908539db5bf65777eacd54b2ab8bd8549`; declaration SHA256: `23dd2cce9cb6c1c0e2c05e799e1f7b5cf14aea72fa5f5802a96485541a779d25`. Device: AMD Radeon AI PRO R9700, driver 32.0.31041.1004, Windows 26200, .NET 10 / Vortice 3.8.3, actual DXC/DXIL 1.9.2602.17. Official source versions/defaults are unchanged; the 50-file upstream lock passes. All builds/tests/GPU collection used the shared validation mutex without driver/power/cache changes.

See `FOCUSED_COST_DIAGNOSTICS.md` for the fixed procedure, `focused-cost-declaration.json` for the frozen schedule, and the delivered `audit.json`, `metrics.csv`, `diagnostic-audit.json`, `provenance-audit.json`, `prior-costs.json`, raw evidence and binary archives for reproducible checks.
