# Native timing quality acceptance ¡ª 2026-09-19

Status: **STOP / not infrastructure-ready**. One authorized attempt, no retries. Native implementation and final Player unchanged. No pilot, feasibility pair, correctness expansion, formal matrix, merge, cleanup, or timing-cadence redesign was performed.

## Operational diagnosis and attempt

At 12:13:37 local time (Asia/Singapore), NVIDIA reported 10% utilization and 42 C. WDDM `pmon` exposed process presence but not per-process utilization; two Windows GPU Engine counter samples produced no entries above 0.5%. These observations do not establish a culprit. Browser, launcher and overlay categories were present; Unity Editor, RenderDoc, PIX and OBS process names were absent. Absence of process names is not proof of attachment state or video playback state, which remain unknown.

Requested normal browser/window closure; stopped game launcher, Unity Hub and crosshair overlay processes. NVIDIA overlay restarted automatically; Intel overlay termination was denied. No service, driver settings, or OS changes. Detailed unrelated process inventory was not committed. Waited 30 seconds before the single attempt. The environment was quiet for the first process, not sustained for the next process. Post-run NVIDIA utilization can include the preceding benchmark sampling window; attribution to background applications or residual benchmark work is **unknown**.

Reused the exact source/binary calibration, 1000-frame correctness prerequisite, CPU/GPU 96-frame integration receipts and placement assessment from v4, copying rather than rerunning or overwriting them. Manifest links the prior evidence and hashes new raw files and executable/plugin. Runtime process ID 47648; run ID and command are retained in raw evidence.

## Required acceptance readout

1. Quiet <=5%: achieved for CPU OFF, snapshots **1%, 0%, 1%**. Not sustained: CPU ON **12%, 7%, 17%** blocked before launch.
2. Load source: not confidently identified. See sanitized operational diagnosis above.
3. CPU overhead: OFF **2874.9335 ms / 2.8749335 ms per frame**, ON unavailable. Relative difference unavailable; reverse order not reached.
4. GPU overhead: OFF and ON not run; unavailable.
5. Instrumentation acceptance: **pending**, neither accepted nor demonstrated material. No subtraction or sparse-cadence change.
6. CPU 1000-frame timestamp pilot: not run; OFF control is not that pilot.
7. CPU pilot resolved triplets: unavailable. OFF intentionally has 0 timestamps.
8. GPU 1000-frame pilot: not run.
9. GPU pilot resolved triplets: unavailable.
10. CPU pilot GPU draw/range: unavailable. OFF draw/range/cull are null.
11. GPU pilot cull/draw/range: unavailable.
12. Pilot ring/delay: unavailable. OFF ring occupancy 0; delay null, not a ring acceptance result. Validator now rejects occupancy 32/32 as well as overflow; no ring enlargement or wait was added.
13. Available control batchCompletionMsPerFrame: **2.8749335**, infrastructure/feasibility-only, not crossover or single-frame latency. Final fence completed; final-submission-to-observation 0.4219 ms, conservative per-frame bound 0.0004219 ms (~0.01468% of batch/frame). Existing start-before-first-CPU-work and final-fence boundaries unchanged.
14. OFF updateIntervalMs p10 **2.76014**, median **2.8591**, mean **2.87486046**, p90 **2.99092**, p95 **3.05298**, population stddev **0.10560159**, CV **0.03673277**. Strongest refresh cluster 349 Hz, fraction 0.66266266 (<0.80); pacing gate passed. Vsync 0, target -1, GC0 collections 0, CPU drift gate passed. Present wait unavailable. No ON/pilot pacing data.
15. OFF pre temperatures **43/43/43 C**, post **45 C / 12%**. ON blocked pre **45/44/44 C**, utilization **12/7/17%**. Timestamped snapshots retained. No post-ON sample because Player never launched.
16. Three balanced pairs: not run; process-level stability and paired ratios unavailable.
17. Correctness: existing final-binary **1/18 conditions**, 100k/25% **10/10 checks** accepted; remaining 17 deferred. No 18/18 claim.
18. Tests: focused Python crossover **12/12**, including new exactly-full ring rejection; final-head repository CI must be checked after push. Previous head CI passed. No unchanged native/Unity rebuild was needed.
19. Source baseline head **767567d836f115481e0b531c6f398c2e5c8385ce**; delivery commit is the commit containing this report (reported after push).
20. Infrastructure-ready: **no**. Quality sequence stopped before complete ON/OFF evidence.
21. Cleanup: no deletion. Provisional categories only, final file inventory deferred until acceptance: A final benchmark/native/runner/test source keep; B final accepted evidence keep (this partial attempt is audit evidence, not a final accepted package); C protocols/reports/manifests keep; D superseded v1/v2/v3 raw diagnostics archival candidates; E generated Player/project/build/capture duplicates transient candidates, preserving unique provenance.
22. Git strategy: provisionally prefer **extract a new clean PR after acceptance**, referencing #12 as audit history. Current #12 has 154 files and 108,567 added lines before this checkpoint, which makes in-place review difficult. Neither strategy executed; #12 stays Draft and unmerged.

Raw evidence: [manifest](../evidence/gpu-timing-quality-20260919/manifest.json), [OFF summary](../evidence/gpu-timing-quality-20260919/overhead-cpu-0-off.summary.json), [blocked ON receipt](../evidence/gpu-timing-quality-20260919/overhead-cpu-0-on.launch.json).

CPU OFF mean host submetrics (ms): cull/list 2.6036101, upload 0.0162819, submit 0.0232209, total 2.6431129. These are one control process, not an arm comparison. Summary distributions now include min/max and population standard deviation; pilot delay min/median/p95/max can be reported when pilots exist.

A future attempt needs explicit authorization and a quieter environment. Investigate a preregistered fixed inter-process settling interval before that attempt if justified; do not loop on utilization, reuse this OFF as an invisible replacement, or weaken the threshold. The existing runner has a conservative first-pair 2% stop; complete reversed-order evidence and an uncertainty/order assessment would still be needed for instrumentation acceptance, and this attempt provides neither.
