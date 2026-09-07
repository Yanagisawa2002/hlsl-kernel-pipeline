# Scan applicability experiment - independent process evidence

Completed the declared first phase: eight exact size/residency cells, five fresh native processes each. **4/8 cells met the predeclared cross-process recommendation gate.** Every planned process is retained; no retry-until-pass or default promotion occurred.

Baseline `18c2e19500063b1749a9bb0315e22d3070a47ade`; actual measurement source `6b66d9da4a8a827dfd329f3f6d3cf6914a9de4b1`. Later analysis/documentation commits are identified separately in the delivery receipt. The original checkout remains at the authorized starting commit.

## Confirmed applicability

Intervals below equally weight five process mean log ratios and use Student-t(df=4). They are pointwise 95% intervals, not a simultaneous eight-cell confidence band. A recommendation requires the identical non-baseline candidate and all five original deployment gates, plus a cross-process lower bound of 1.01. An interval from mixed selected candidates is deliberately not pooled.

| Exact cell | Selected backends by process | Individual gates | Process speedup and 95% CI | Decision |
|---|---|---:|---:|---|
| scan-4Mi-slots1 | 1,2,1,2,2 | 2/5 | Not comparable: mixed selection | inconclusive |
| scan-4Mi-slots3 | 1,1,1,2,1 | 2/5 | Not comparable: mixed selection | inconclusive |
| scan-8Mi-slots1 | 1,1,1,1,1 | 3/5 | 0.9988x [0.9904, 1.0073] | inconclusive |
| scan-8Mi-slots3 | 3,3,3,3,3 | 5/5 | 1.1063x [1.0997, 1.1129] | confirmed_gain |
| scan-12Mi-slots1 | 3,3,3,3,3 | 5/5 | 1.1005x [1.0851, 1.1160] | confirmed_gain |
| scan-12Mi-slots3 | 3,3,3,3,3 | 5/5 | 1.2815x [1.2640, 1.2993] | confirmed_gain |
| scan-16Mi-slots1 | 3,3,3,3,3 | 5/5 | 1.5073x [1.4864, 1.5285] | confirmed_gain |
| scan-16Mi-slots3 | 3,3,3,3,3 | 4/5 | 1.4976x [1.4524, 1.5441] | inconclusive |

Backend 1 is the existing LDS/hierarchical Scan, backend 2 its wave32 variant, and backend 3 the existing persistent single-pass wave32 implementation. All configurations use group 256, four elements/thread, scalar loads and uint addition; backend 3 uses scale 4 and 256 persistent groups. Baseline self-control intervals are not improvements. Recommendations, if any, apply only to the precise rows passing the gate. Inconclusive rows do not establish a reliable gain or a global absence of benefit.

No adjacent-cell pair satisfied the declared independent-confirmation refinement trigger. No midpoint was sampled, and no unmeasured global crossover threshold is inferred. Historical 16M gains were development context only and were not mixed with these new processes.

## Tail latency and complete-plan cost

Each process confirmation has 16 baseline and 16 selected-candidate samples. Each sample is GPU time for a batch of 36 complete plans divided by 36, with an equal cycle through one or three resident slots. P95/P99 use linear interpolation within each process. Below, comparable selections show the arithmetic mean of the five process quantiles; the complete JSON also includes their df=4 confidence intervals and the per-process quantiles for every cell. Mixed selections remain available per process in the CSV and JSON. P99 from 16 samples lies near the observed maximum and does not establish a hard single-frame latency bound.

| Cell | Baseline P95 ms | Candidate P95 ms | Baseline P99 ms | Candidate P99 ms |
|---|---:|---:|---:|---:|
| scan-4Mi-slots1 | mixed; see process CSV | mixed; see process CSV | mixed; see process CSV | mixed; see process CSV |
| scan-4Mi-slots3 | mixed; see process CSV | mixed; see process CSV | mixed; see process CSV | mixed; see process CSV |
| scan-8Mi-slots1 | 0.100310 | 0.099624 | 0.102992 | 0.099781 |
| scan-8Mi-slots3 | 0.172395 | 0.156888 | 0.172649 | 0.157063 |
| scan-12Mi-slots1 | 0.251975 | 0.229304 | 0.255885 | 0.229798 |
| scan-12Mi-slots3 | 0.291688 | 0.227532 | 0.294136 | 0.227882 |
| scan-16Mi-slots1 | 0.450191 | 0.299553 | 0.450832 | 0.299686 |
| scan-16Mi-slots3 | 0.456460 | 0.338237 | 0.464700 | 0.381035 |

| Cell | Backend | Complete-plan passes | Logical bytes/slot | Logical bytes/arm | Committed bytes/arm |
|---|---:|---:|---:|---:|---:|
| scan-4Mi-slots1 | 1 | 5 | 33,587,236 | 33,587,236 | 33,947,648 |
| scan-4Mi-slots1 | 2 | 5 | 33,587,236 | 33,587,236 | 33,947,648 |
| scan-4Mi-slots1 | 3 | 2 | 33,566,728 | 33,566,728 | 33,685,504 |
| scan-4Mi-slots3 | 1 | 5 | 33,587,236 | 100,761,708 | 101,842,944 |
| scan-4Mi-slots3 | 2 | 5 | 33,587,236 | 100,761,708 | 101,842,944 |
| scan-4Mi-slots3 | 3 | 2 | 33,566,728 | 100,700,184 | 101,056,512 |
| scan-8Mi-slots1 | 1 | 5 | 67,174,468 | 67,174,468 | 67,502,080 |
| scan-8Mi-slots1 | 2 | 5 | 67,174,468 | 67,174,468 | 67,502,080 |
| scan-8Mi-slots1 | 3 | 2 | 67,133,448 | 67,133,448 | 67,239,936 |
| scan-8Mi-slots3 | 1 | 5 | 67,174,468 | 201,523,404 | 202,506,240 |
| scan-8Mi-slots3 | 2 | 5 | 67,174,468 | 201,523,404 | 202,506,240 |
| scan-8Mi-slots3 | 3 | 2 | 67,133,448 | 201,400,344 | 201,719,808 |
| scan-12Mi-slots1 | 1 | 5 | 100,761,700 | 100,761,700 | 101,056,512 |
| scan-12Mi-slots1 | 2 | 5 | 100,761,700 | 100,761,700 | 101,056,512 |
| scan-12Mi-slots1 | 3 | 2 | 100,700,168 | 100,700,168 | 100,794,368 |
| scan-12Mi-slots3 | 1 | 5 | 100,761,700 | 302,285,100 | 303,169,536 |
| scan-12Mi-slots3 | 2 | 5 | 100,761,700 | 302,285,100 | 303,169,536 |
| scan-12Mi-slots3 | 3 | 2 | 100,700,168 | 302,100,504 | 302,383,104 |
| scan-16Mi-slots1 | 1 | 5 | 134,348,932 | 134,348,932 | 134,610,944 |
| scan-16Mi-slots1 | 2 | 5 | 134,348,932 | 134,348,932 | 134,610,944 |
| scan-16Mi-slots1 | 3 | 2 | 134,266,888 | 134,266,888 | 134,348,800 |
| scan-16Mi-slots3 | 1 | 5 | 134,348,932 | 403,046,796 | 403,832,832 |
| scan-16Mi-slots3 | 2 | 5 | 134,348,932 | 403,046,796 | 403,832,832 |
| scan-16Mi-slots3 | 3 | 2 | 134,266,888 | 402,800,664 | 403,046,400 |

The interval includes every reset, scan and resource transition in the complete plan. Upload, PSO creation, post-timing poison/re-execution, readback and host oracles are excluded. No isolated-kernel timing is substituted. Each arm is capped at 512 MiB, so two simultaneous arms remain below 1 GiB. Rotating slots hold real distinct deterministic inputs; normal WDDM placement, uncontrolled cache state and DXGI budgets do not prove pinned or cache-cold residency.

## Verification and experiment identity

Release solution and the new correctness harness built with zero warnings/errors. Core regression: 91 passed, zero failed/skipped. Separate non-aligned native correctness: 24 scenarios at 4 Mi+3, 8 Mi+127, 12 Mi+1 and 16 Mi+4095, both slot counts and all three fixed candidates; 96/96 output/poison checks passed. Formal data: 40 independent processes, 3,840 observations and 15,360 output/poison checks, independently audited. No runtime/shader implementation or Unity package changed in this round; a new Unity run was therefore unnecessary.

The offline analyzer independently replays paired ratios/intervals, within- and between-phase drift, CV, complete batch semantics, seeds and actual input hashes across processes, all declared outputs and both poison attempts, source graphs, manifests, checkpoints, profiles, native compiler hashes and PID/start/session identity. Seven separate statistical regression checks cover equal-process intervals, missing/duplicate rounds, mixed selections, one rejected process, tail interpolation and baseline-only non-promotion. An early analyzer assumed 128 rather than the protocol's actual 96 observations (two challenger calibration comparisons plus one confirmation); that analysis-development failure is retained. The analyzer was corrected; measured data and acceptance gates were unchanged.

All five repetitions and round order were declared before native sampling in `declaration.json` (SHA-256 `f1cbbff66ef66cf71550d3bd06fc8521e241026642ebc5198a1f4085bf3c3185`). Fresh calibration seeds begin at 60,000,000, independent confirmation is offset by 5,000, and correctness seeds begin at 90,000,000. Eight ABBA/BAAB blocks per challenger in calibration, eight for the locked confirmation; CV <= 0.05, drift <= 0.15, candidate p95 no worse than baseline and lower confidence bound >= 1.01 were retained. Every rejected, noisy or degraded process remains in the archive.

Measured hardware: AMD Radeon AI PRO R9700, driver 32.0.31041.1004, D3D12, shader model 6.6, Windows build 26200. Native DXC 1.9.2602.17 and the exact loaded x64 DLL hashes are recorded per observation. Source, binaries and protocol hashes were frozen and verified before each process. The 37 declared runtime files include dependencies and other packaged runtime architectures; they are not all claimed as loaded modules.

Every heavy build, correctness execution and formal GPU process held the shared `Local\CodexR9700VNextUnityGpu` mutex until exit. Queue time is not GPU plan time. User applications were left running, no caches or power/driver settings changed, and thermal/clock controls and runtime occupancy/bandwidth counters were unavailable. External interference and process snapshots are retained. This is a bounded same-host experiment, not a multi-driver or multi-day generalization.

## Handoff and replay

Phase 1 is complete. Radix remains conditional and has not run. Its prepared controls and open preflight detail are in `RADIX_PHASE2_PREPARATION.md`; the parent must dispatch phase 2 after all first phases complete. No new default, original-checkout merge or push was performed.

Deliverables: this report, `scan-phase1-audit.json`, `scan-phase1-process-summary.csv`, `scan-phase1-evidence-index.json`, `SHA256SUMS.txt`, the delivery receipt and `scan-phase1-raw-evidence.zip`. The archive includes original manifests, all raw reports/checkpoints/profiles/CSV/HTML, native stdout/stderr and execution receipts, non-aligned correctness, build/test evidence, frozen runtime/source snapshots and analysis code. Every entry was read back and SHA-256 verified.

Archive: 601 files, 44,538,407 compressed bytes; SHA-256 `a1bc552843abe01423ae6798c891f4427f8c9dcd76ce82685647f167c6de44ad`.

Replay from a clean delivery checkout (whose native code matches the measured source) using the explicit shared runner path: build the Release solution and `tools/HlslPerf.ScanCorrectness` under the mutex, then run `python tools/declare_scan_process_matrix.py <new-root>`. Execute `tools/Invoke-ScanProcessMatrix.ps1 -Phase Correctness` followed by `-Phase Measure`, passing `-EvidenceRoot`, `-SerializedValidationRunner` and a separate `-CoordinationReport`. Audit with `python tools/analyze_scan_process_matrix.py <repo> <root> <audit.json>`. New attempts require fresh outputs and declarations; completed evidence must not be overwritten. Historical evidence is diagnostic context only.
