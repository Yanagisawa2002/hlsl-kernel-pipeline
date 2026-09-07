# Radix phase-two results

Executed all **80/80 baseline preflight processes** across 16 cells. **10/16 cells qualified**, so exactly **50 new comparison processes** ran; 6 cells remained not applicable for wide comparison. **9 exact cells confirmed a deployable non-baseline gain** under all five process gates. No control replacement, retry-until-pass, silent size reduction, default promotion, original-branch merge or push occurred.

Actual measurement source: `41d143cbf530128684a17ba989a55457a516049b`. Phase-two start: `f787f741becf7a792c5a39ea86c58361c3ca86cb`; original project baseline: `18c2e19500063b1749a9bb0315e22d3070a47ade`. Scan source/binaries and its completed report were preserved. Later analysis/report commits are recorded separately in the delivery receipt.

## Baseline eligibility for all planned cells

Keys: fixed binary group 256, four items/thread, scalar loads, wave32 backend 2. Pairs: group 128, four items/thread, scalar loads, wave64 backend 2. These are frozen historical controls, not a new exhaustive optimum. Their exact hierarchy/pass counts are included below; the result must not be extrapolated to an unmeasured globally best binary configuration.

Uniform uses full uint keys; duplicate-heavy uses seven values 0..6 while retaining all 32 digit bits and all complete-plan passes. Each process uses fresh actual inputs, eight baseline self-control calibration blocks and eight independent confirmation blocks. Entry requires all five calibration/confirmation/correctness/stability gates, plus cross-process baseline median CV <= 0.05 and max/min drift <= 0.15. Self-control ratios are diagnostic, never gains.

| Planned cell | Individual preflight gates | Median CV | Median range drift | Self-control ratio, 95% CI | Entry decision |
|---|---:|---:|---:|---:|---|
| radix-1Mi-keys-uniform-slots1 | 4/5 | 0.0051 | 0.0129 | 1.0081x [0.9917, 1.0248] | inconclusive |
| radix-1Mi-keys-uniform-slots3 | 5/5 | 0.0023 | 0.0058 | 1.0083x [1.0031, 1.0134] | eligible |
| radix-1Mi-keys-duplicate-slots1 | 2/5 | 0.0083 | 0.0201 | 1.0109x [0.9943, 1.0278] | inconclusive |
| radix-1Mi-keys-duplicate-slots3 | 2/5 | 0.0050 | 0.0123 | 0.9966x [0.9839, 1.0095] | inconclusive |
| radix-1Mi-pairs-uniform-slots1 | 5/5 | 0.0024 | 0.0058 | 0.9976x [0.9856, 1.0098] | eligible |
| radix-1Mi-pairs-uniform-slots3 | 4/5 | 0.0845 | 0.2026 | 1.0038x [0.9967, 1.0109] | inconclusive |
| radix-1Mi-pairs-duplicate-slots1 | 5/5 | 0.0029 | 0.0073 | 0.9993x [0.9916, 1.0070] | eligible |
| radix-1Mi-pairs-duplicate-slots3 | 5/5 | 0.0060 | 0.0135 | 1.0004x [0.9972, 1.0036] | eligible |
| radix-4Mi-keys-uniform-slots1 | 5/5 | 0.0023 | 0.0056 | 0.9940x [0.9875, 1.0005] | eligible |
| radix-4Mi-keys-uniform-slots3 | 4/5 | 0.0038 | 0.0107 | 0.9913x [0.9707, 1.0123] | inconclusive |
| radix-4Mi-keys-duplicate-slots1 | 5/5 | 0.0040 | 0.0098 | 0.9992x [0.9954, 1.0029] | eligible |
| radix-4Mi-keys-duplicate-slots3 | 4/5 | 0.0029 | 0.0072 | 0.9997x [0.9987, 1.0007] | inconclusive |
| radix-4Mi-pairs-uniform-slots1 | 5/5 | 0.0336 | 0.0647 | 0.9972x [0.9960, 0.9984] | eligible |
| radix-4Mi-pairs-uniform-slots3 | 5/5 | 0.0330 | 0.0639 | 0.9982x [0.9952, 1.0013] | eligible |
| radix-4Mi-pairs-duplicate-slots1 | 5/5 | 0.0337 | 0.0664 | 1.0069x [1.0054, 1.0084] | eligible |
| radix-4Mi-pairs-duplicate-slots3 | 5/5 | 0.0331 | 0.0652 | 1.0033x [1.0008, 1.0059] | eligible |

All failed/noisy processes remain in the raw evidence. A later success does not replace an earlier failed gate. The immutable entry ledger was written only after all 80 preflight processes and independent audit; it binds every raw report/receipt hash. Conditional manifests, configurations, seeds and their full order were already frozen before preflight.

## Conditional complete-plan comparison

Wide 8-bit configuration: group 128, two items/thread, scalar loads, wave32 backend 2. The same wide configuration at 4 bits appears only in the two declared 4 Mi uniform three-slot keys/pairs controls, and only if that cell qualified. It was not moved to another cell when a preflight failed.

| Eligible cell | Selected bits by process | Individual gates | Process speedup and pointwise 95% CI | Decision |
|---|---|---:|---:|---|
| radix-1Mi-keys-uniform-slots3 | 8,8,8,8,8 | 5/5 | 1.5784x [1.5577, 1.5994] | confirmed_gain |
| radix-1Mi-pairs-uniform-slots1 | 8,8,8,8,8 | 5/5 | 2.7862x [2.7562, 2.8166] | confirmed_gain |
| radix-1Mi-pairs-duplicate-slots1 | 1,8,8,8,8 | 5/5 | Not comparable / unavailable | inconclusive |
| radix-1Mi-pairs-duplicate-slots3 | 8,8,8,8,8 | 5/5 | 3.0033x [2.9851, 3.0216] | confirmed_gain |
| radix-4Mi-keys-uniform-slots1 | 8,8,8,8,8 | 5/5 | 1.3458x [1.3405, 1.3511] | confirmed_gain |
| radix-4Mi-keys-duplicate-slots1 | 8,8,8,8,8 | 5/5 | 1.3699x [1.3588, 1.3812] | confirmed_gain |
| radix-4Mi-pairs-uniform-slots1 | 8,8,8,8,8 | 5/5 | 5.9538x [5.9071, 6.0008] | confirmed_gain |
| radix-4Mi-pairs-uniform-slots3 | 8,8,8,8,8 | 5/5 | 6.0121x [5.9466, 6.0782] | confirmed_gain |
| radix-4Mi-pairs-duplicate-slots1 | 8,8,8,8,8 | 5/5 | 6.8590x [6.8080, 6.9102] | confirmed_gain |
| radix-4Mi-pairs-duplicate-slots3 | 8,8,8,8,8 | 5/5 | 6.8791x [6.7859, 6.9736] | confirmed_gain |

`radix-1Mi-pairs-duplicate-slots1` remains inconclusive. Five valid individual confirmations can include a retained baseline; differing selections do not establish five confirmations of one wide candidate. Process 1 8-bit calibration: Raw timing variation exceeded the declared budget (samples retained). Candidate CV=0.055095.

A recommendation requires the identical non-baseline candidate in all five independent confirmations, every original individual deployment gate, and an equally weighted process log-ratio interval lower bound >= 1.01. Intervals use Student-t(df=4); they are pointwise, not a simultaneous 16-cell confidence band. Mixed selections are not pooled. Calibration comparisons are exploratory and retained separately in the JSON. The selected candidate is locked before fresh-seed confirmation.

| Not-applicable cell | Unexecuted conditional processes | Reason |
|---|---:|---|
| radix-1Mi-keys-uniform-slots1 | 5 | At least one of five baseline calibration/confirmation/stability gates failed |
| radix-1Mi-keys-duplicate-slots1 | 5 | At least one of five baseline calibration/confirmation/stability gates failed |
| radix-1Mi-keys-duplicate-slots3 | 5 | At least one of five baseline calibration/confirmation/stability gates failed |
| radix-1Mi-pairs-uniform-slots3 | 5 | At least one of five baseline calibration/confirmation/stability gates failed; Cross-process baseline median CV exceeded 0.05; Cross-process baseline median range drift exceeded 0.15 |
| radix-4Mi-keys-uniform-slots3 | 5 | At least one of five baseline calibration/confirmation/stability gates failed |
| radix-4Mi-keys-duplicate-slots3 | 5 | At least one of five baseline calibration/confirmation/stability gates failed |

### Declared 4-bit control: calibration only

The declared 4 Mi uniform keys / three-slot control was not applicable after preflight. In the declared 4 Mi uniform pairs / three-slot control, 4-bit passed all five calibration comparisons, but all five processes selected 8-bit for fresh confirmation. The following process-local df=7 intervals are exploratory; no independent 4-bit confirmation was executed.

| Process | 4-bit calibration speedup, 95% CI | Candidate CV | Calibration gate |
|---|---:|---:|---|
| 1 | 4.0502x [4.0222, 4.0784] | 0.011256 | passed |
| 2 | 3.9744x [3.8620, 4.0901] | 0.047029 | passed |
| 3 | 4.0012x [3.9741, 4.0285] | 0.010723 | passed |
| 4 | 4.0203x [3.9877, 4.0532] | 0.012419 | passed |
| 5 | 4.0052x [3.9869, 4.0237] | 0.012250 | passed |

## Latency, passes and memory

Each sample is the timestamp duration of 18 complete plans divided by 18. Each ring slot executes equally often. All digit, histogram/flag, scan hierarchy, scatter, pair-output split and resource transition work is inside the GPU interval. Upload, PSO creation, poison/readback and CPU oracle work are excluded. These are amortized GPU plan costs, not host end-to-end time or single application-frame latency.

P95/P99 use linear interpolation of 16 samples per confirmation arm per process. The table shows equal-process mean quantiles for comparable selections; JSON retains process values and df=4 intervals. Mixed selections are available per process in the CSV. P99 is close to the observed maximum in this small sample and is not a hard bound.

| Cell | Baseline median ms | Selected median ms | Baseline P95 ms | Selected P95 ms | Baseline P99 ms | Selected P99 ms |
|---|---:|---:|---:|---:|---:|---:|
| radix-1Mi-keys-uniform-slots3 | 1.095776 | 0.696556 | 1.146263 | 0.733962 | 1.159395 | 0.742597 |
| radix-1Mi-pairs-uniform-slots1 | 1.949548 | 0.693289 | 2.009774 | 0.736116 | 2.021905 | 0.737651 |
| radix-1Mi-pairs-duplicate-slots1 | see process CSV | see process CSV | see process CSV | see process CSV | see process CSV | see process CSV |
| radix-1Mi-pairs-duplicate-slots3 | 2.183453 | 0.726529 | 2.200953 | 0.740025 | 2.220106 | 0.746243 |
| radix-4Mi-keys-uniform-slots1 | 3.492735 | 2.586798 | 3.548962 | 2.677359 | 3.551566 | 2.699952 |
| radix-4Mi-keys-duplicate-slots1 | 3.512357 | 2.552790 | 3.532538 | 2.635788 | 3.535113 | 2.647185 |
| radix-4Mi-pairs-uniform-slots1 | 16.514340 | 2.767290 | 16.590028 | 2.818709 | 16.597084 | 2.840332 |
| radix-4Mi-pairs-uniform-slots3 | 16.596437 | 2.755784 | 16.805264 | 2.825729 | 16.816967 | 2.833244 |
| radix-4Mi-pairs-duplicate-slots1 | 18.504578 | 2.684285 | 18.626228 | 2.762747 | 18.645160 | 2.781528 |
| radix-4Mi-pairs-duplicate-slots3 | 18.435658 | 2.676346 | 18.759333 | 2.771077 | 18.777112 | 2.802605 |

| Cell | Bits | Digit iterations | Dispatches | Logical bytes/slot | Plan scratch bytes/slot | Committed bytes/arm |
|---|---:|---:|---:|---:|---:|---:|
| radix-1Mi-keys-uniform-slots1 | 1 | 32 | 160 | 20,979,716 | 12,591,108 | 21,233,664 |
| radix-1Mi-keys-uniform-slots3 | 1 | 32 | 160 | 20,979,716 | 12,591,108 | 63,700,992 |
| radix-1Mi-keys-uniform-slots3 | 8 | 4 | 28 | 21,004,420 | 12,615,812 | 64,094,208 |
| radix-1Mi-keys-duplicate-slots1 | 1 | 32 | 160 | 20,979,716 | 12,591,108 | 21,233,664 |
| radix-1Mi-keys-duplicate-slots3 | 1 | 32 | 160 | 20,979,716 | 12,591,108 | 63,700,992 |
| radix-1Mi-pairs-uniform-slots1 | 1 | 32 | 225 | 41,959,460 | 25,182,244 | 42,336,256 |
| radix-1Mi-pairs-uniform-slots1 | 8 | 4 | 29 | 41,975,940 | 25,198,724 | 42,336,256 |
| radix-1Mi-pairs-uniform-slots3 | 1 | 32 | 225 | 41,959,460 | 25,182,244 | 127,008,768 |
| radix-1Mi-pairs-duplicate-slots1 | 1 | 32 | 225 | 41,959,460 | 25,182,244 | 42,336,256 |
| radix-1Mi-pairs-duplicate-slots1 | 8 | 4 | 29 | 41,975,940 | 25,198,724 | 42,336,256 |
| radix-1Mi-pairs-duplicate-slots3 | 1 | 32 | 225 | 41,959,460 | 25,182,244 | 127,008,768 |
| radix-1Mi-pairs-duplicate-slots3 | 8 | 4 | 29 | 41,975,940 | 25,198,724 | 127,008,768 |
| radix-4Mi-keys-uniform-slots1 | 1 | 32 | 224 | 83,918,884 | 50,364,452 | 84,279,296 |
| radix-4Mi-keys-uniform-slots1 | 8 | 4 | 28 | 84,017,668 | 50,463,236 | 84,279,296 |
| radix-4Mi-keys-uniform-slots3 | 1 | 32 | 224 | 83,918,884 | 50,364,452 | 252,837,888 |
| radix-4Mi-keys-duplicate-slots1 | 1 | 32 | 224 | 83,918,884 | 50,364,452 | 84,279,296 |
| radix-4Mi-keys-duplicate-slots1 | 8 | 4 | 28 | 84,017,668 | 50,463,236 | 84,279,296 |
| radix-4Mi-keys-duplicate-slots3 | 1 | 32 | 224 | 83,918,884 | 50,364,452 | 252,837,888 |
| radix-4Mi-pairs-uniform-slots1 | 1 | 32 | 225 | 167,837,828 | 100,728,964 | 168,165,376 |
| radix-4Mi-pairs-uniform-slots1 | 8 | 4 | 29 | 167,903,748 | 100,794,884 | 168,165,376 |
| radix-4Mi-pairs-uniform-slots3 | 1 | 32 | 225 | 167,837,828 | 100,728,964 | 504,496,128 |
| radix-4Mi-pairs-uniform-slots3 | 4 | 8 | 57 | 136,323,108 | 69,214,244 | 410,124,288 |
| radix-4Mi-pairs-uniform-slots3 | 8 | 4 | 29 | 167,903,748 | 100,794,884 | 504,496,128 |
| radix-4Mi-pairs-duplicate-slots1 | 1 | 32 | 225 | 167,837,828 | 100,728,964 | 168,165,376 |
| radix-4Mi-pairs-duplicate-slots1 | 8 | 4 | 29 | 167,903,748 | 100,794,884 | 168,165,376 |
| radix-4Mi-pairs-duplicate-slots3 | 1 | 32 | 225 | 167,837,828 | 100,728,964 | 504,496,128 |
| radix-4Mi-pairs-duplicate-slots3 | 8 | 4 | 29 | 167,903,748 | 100,794,884 | 504,496,128 |

Plan scratch means allocated uninitialized buffers excluding input and declared verified outputs; it is not RGA private scratch, register spilling or measured occupancy. The native constructor enforces 512 MiB per arm for committed DEFAULT plan/constant buffers (two arms <= 1 GiB). This is not a bound on host oracle memory, upload/readback heaps or driver caches. Actual allocation failures, if present, retain the requested size and slots. Normal WDDM residency and real input rotation do not establish pinned residency or guaranteed cache-cold state.

## Audit, provenance and retained limitations

Preflight: 5,120 observations, 30,720 verified output/poison checks, 80/80 supported/correct processes. Comparison: 3,360 observations, 21,760 checks, 50/50 supported/correct processes. Sorted keys and original-index payloads are independently verified where applicable. Both poison values and every declared output/slot are audited.

The independent analyzer replays schedule/order, process identities, actual cross-process and cross-stage input separation, every output hash, schemas, source graphs, compiler files, paired Student-t intervals, CV, within/between-phase drift, p95 and five-process entry/benefit gates. Eleven statistical regression tests cover rejected calibration, unstable summary, process drift/CV, unsupported memory cases, incomplete rounds, mixed selections, one rejected process, baseline non-promotion and interval/quantile arithmetic.

Declaration SHA-256: `4c78685cda9e226c117fbc8459473ae48cb808fb04e1b6a52dfaa64006dccbd3`. Entry ledger SHA-256: `01310e696e16343d3ec9ee68d8fd7e91d1512e93dd94a85ec2c6a545c22ddaeb`. Preflight seeds begin at 100,000,000 and comparison at 300,000,000, with disjoint cell/round offsets and confirmation 5,000 later. All 160 manifests passed JSON-schema and exact candidate-expansion checks before GPU sampling.

AMD Radeon AI PRO R9700, driver 32.0.31041.1004, Windows build 26200, D3D12 SM6.6, native DXC 1.9.2602.17. Exact executable/dependency/loaded-compiler hashes, process PID/start/exit times and external-application snapshots are retained. Every heavy build and native process used the shared mutex through process exit. Temperature, clocks, physical cache/residency state and runtime occupancy/bandwidth/spill counters were unavailable or uncontrolled. No causal claim about those unavailable metrics is inferred from timing.

The isolated Release CLI build passed with zero warnings/errors. No runtime, shader or Unity package code changed from the Scan-delivered implementation; its 91 Core tests remain the applicable code regression, not a newly claimed rerun. An initial declaration was blocked before any GPU sampling because Python imported a local bytecode cache into the clean checkout. The declaration tool was fixed, the first build/log and local cache were preserved, and a new isolated build was frozen. All 37 Scan runtime hashes remained unchanged.

## Evidence and reproduction

The raw archive contains 1,612 files (33,700,954 compressed bytes), including declarations, entry ledger, every executed report/checkpoint/profile/CSV/stdout/stderr/receipt, blocked first-declaration evidence and both successful build logs, frozen runtime/source and analyzer code. Every entry was read back and checked against SHA-256. Archive SHA-256: `166ae3d75bbee9166e5a68be89bb4c2bcb6b0f7b167c8b3ea73f6698f451edc0`.

Use a fresh evidence/build/output directory. Build the Release CLI under `Invoke-SerializedValidation.ps1` with `--artifacts-path <new-build>/artifacts`. Run `python tools/declare_radix_process_matrix.py <new-root> --runtime <isolated-cli-directory>`. Execute `tools/Invoke-RadixProcessMatrix.ps1 -Phase preflight` with `-EvidenceRoot`, `-SerializedValidationRunner` and a fresh `-CoordinationReport` initialized to an empty JSON object (`{}`). Audit all 80 processes and seal with `python tools/analyze_radix_process_matrix.py <repo> <root> preflight <audit.json> --seal-entry`. Then execute `-Phase comparison`, which filters the frozen schedule by that entry ledger, and audit the comparison phase. Never overwrite prior attempts or use confirmation to replace a control.
