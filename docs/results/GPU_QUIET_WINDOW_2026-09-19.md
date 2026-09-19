# Quiet-window continuation 〞 2026-09-19

**STOP: CPU ON failed the existing GPU drift gate. Infrastructure is not ready.** Native timestamp implementation, Player binary and workload are unchanged. The observer found a candidate; the independent formal gate passed. Timestamp completeness succeeded, but the complete process is rejected for quality. No samples were discarded or repaired. No retry, reversed pair, GPU overhead, pilot, feasibility pairs, correctness expansion, formal study, merge, cleanup or branch extraction followed.

## Operational helper

[Usage](../../unity/GpuDrivenCrowdBenchmark/QUIET_WINDOW.md): `python tools/watch_gpu_quiet.py --threshold 5 --consecutive 10 --interval 1`. The helper queries only anonymous aggregate utilization and temperature. It does not launch a benchmark or write formal evidence. Default waits until success/interruption; optional timeout is operator supplied. Five tests cover the rejected complete timestamp run and exercise all-adapter streak/reset, query failure, timeout/no launch and aggregate-only query; tests are integrated into CI.

Observer ran 04:25:35.549543每04:28:05.735256 UTC, 143 observations over 150.238 seconds. One candidate window reached ten successive 0% / 45 C samples, spanning 04:27:56.231010每04:28:05.735256 UTC (9.504246 seconds). Earlier quiet streaks were interrupted. The observer stops at success, so complete window duration and continuous inactivity between samples are unknown. Observed range 0每38%, 45每46 C. Windows GPU Engine query failed with a buffer error; no reliable background source identified, no private process inventory committed.

## Fixed sequence and disposition

Existing accepted CPU pair A OFF is preserved byte-for-byte, **2874.9335 ms total / 2.8749335 ms per frame**. This continuation attempted only pair A ON after rechecking the original calibration, correctness, integration and placement prerequisites against source identity. Separate attempt directory and original blocked receipts retained. Next predetermined controls would have been pair B ON then OFF, but they are not authorized to proceed past this failure.

CPU ON process 59040, run `a45c1859-d370-447c-8c2e-add9b26383cf`: pre-launch utilization **0/0/0%**, temperatures **45/45/45 C**; post-run **9% / 44 C**. Exit 0, batch complete, 1000/1000 timestamps, invalid 0, ring max 3, frequency 1 GHz. ID/slot/tick/fence checks pass. CPU gpuCullMs stays null. Delay min/median/p95/max **1/2/2/3 frames**. No native failure was identified.

The GPU range drift metric is `abs(mean(last 250) / mean(first 250) - 1)`. First mean **0.061509632 ms**, last mean **0.116867072 ms**, drift **89.998002%**, exceeding 15%. CPU drift **0.396699%**. The whole ON process is rejected. Reason is not attributed to background load, clock ramping or instrumentation without evidence.

## Descriptive data from rejected ON 〞 not an accepted A/B comparison

ON batch **3031.9902 ms / 3.0319902 ms per frame**. Arithmetic versus historical OFF is **5.462968%**, but this is **not an accepted relative instrumentation effect** because ON failed quality and reversed ordering is absent. Do not use it to decide that overhead is material, subtract it, or implement sparse cadence.

ON pacing p10/median/mean/p90/p95 = **2.86896 / 3.01190 / 3.03198789 / 3.19990 / 3.28205 ms**; population stddev **0.16999961 ms**, CV **5.60687%**. Strongest refresh cluster 334 Hz, fraction 0.54254254, pacing passed. GC0=0; final-fence observation bound **0.00048 ms/frame**, 0.48 ms aggregate, about 0.01583% of batch. Pre/post thermal change -1 C, same adapter/driver (post-hoc check; runner stopped at drift before its thermal check). Present wait unavailable.

ON GPU draw mean/median/p95 **0.091075584 / 0.095232 / 0.117760 ms**; range **0.091079680 / 0.095232 / 0.117760 ms**. These summarize rejected process diagnostics only. Host mean cull/list **2.6192179**, upload **0.0216685**, submit **0.0480735**, total **2.6889599 ms**. No CPU/GPU comparison.

## Requested status (23 items)

1. Observer added: yes.
2. Candidate <=5% window: yes, one in 143 observations, sampled span 9.504246 s.
3. Background source: not confidently known.
4. Blocked formal launches: **0 new; 2 historical** (20/16/11%, then 12/7/17%). Separately **1 new post-launch quality failure** (GPU drift).
5. Accepted CPU order: **OFF only**. Attempted OFF -> ON; ON rejected. Reverse ON -> OFF not reached.
6. CPU batch/frame: accepted OFF **2.8749335**; rejected ON **3.0319902** ms.
7. CPU relative overhead: unavailable as a valid estimate; diagnostic arithmetic **5.462968%**, excluded.
8. CPU instrumentation accepted: **no / unresolved**.
9. GPU accepted order: none.
10. GPU OFF/ON batch/frame: unavailable.
11. GPU relative overhead: unavailable.
12. GPU instrumentation accepted: **no / not reached**.
13. ON completeness: only new ON process **1000/1000**, invalid 0, IDs/slots valid. Quality failed independently.
14. Max ring occupancy: **3/32**, delay 1/2/2/3 min/median/p95/max.
15. Pacing: ON passed; metrics above. OFF retained from previous report.
16. Background/temp: independent gate 0/0/0%, 45 C; post 9%, 44 C. No confident attribution.
17. Single CPU pilot: not reached.
18. Single GPU pilot: not reached.
19. Three feasibility pairs: not reached.
20. 18/18 correctness: not reached; existing 100k/25% 10/10 checks retained (1/18 conditions).
21. Tests: **37 Python tests passed locally**. Final-head CI is checked after push and reported in PR Checks; unchanged native/Unity builds are not rerun.
22. Baseline head **4e37b0f876807f373b2a92b09cc661de662bc603**; updated head is the delivery commit containing this report.
23. Infrastructure-ready: **no**. PR #12 stays Draft; no formal experiment or extraction.

[Evidence manifest](../evidence/gpu-quiet-window-20260919/manifest.json). New raw ON JSON is losslessly gzip-compressed to reduce new diff size; SHA-256 covers compressed and original bytes. No existing evidence was changed or removed. Operational-summary JSON is a console rollup, explicitly not formal gate evidence. Future accepted controls require explicit continuation after investigating the quality failure; no automatic retry loop was added.
