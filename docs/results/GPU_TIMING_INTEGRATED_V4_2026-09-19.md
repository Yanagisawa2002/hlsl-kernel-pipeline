# Native timing integrated into the real workload: quality checkpoint blocked

The actual CPU/GPU `CrossoverBenchmark.Draw` now uses the selected native D3D12 backend. Both real-workload short diagnostics passed 96/96 native triplets, and RenderDoc verified their command placement. **The first timestamp-OFF overhead process was blocked before launch by GPU utilization 20%, 16%, 11% (limit <=5%).** No performance retries, ON/OFF estimates, 1000-frame pilots, three-pair study, remaining correctness matrix or formal 216-process matrix followed. PR #12 remains Draft and is not infrastructure-merge-ready. No crossover is claimed.

## Requested checkpoint results

| # | Item | Result |
|---|---|---|
| 1 | Native integration | Implemented in the real CPU and GPU draw paths, selected by default; legacy Recorder rejected for actual benchmark timing |
| 2 | Exact stages | GPU T0 -> reset/dispatch -> T1 -> counter copy/indirect draw -> T2. CPU T0 -> T1 -> direct draw -> T2, gpuCullMs=null |
| 3 | Placement verification | RenderDoc API chunks verified both paths; capture-only extended warmup executes the same Draw method; no capture duration used |
| 4 | Timestamp frequency | 1,000,000,000 Hz in both real short diagnostics |
| 5 | Timestamp completion | CPU 96/96 and GPU 96/96 measured triplets; 300 warmup triplets per process also consumed; 1000/1000 acceptance not reached |
| 6 | Maximum ring occupancy | CPU 2/32; GPU 3/32, including warmup |
| 7 | Resolution delay | CPU mean/median/p10/p90/p95 = 1.948/2/2/2/2 frames; GPU = 2.073/2/2/3/3; diagnostic metadata only |
| 8 | Per-frame synchronization | None: nonblocking reads after completed native fences; full ring throws; only initialization/warmup/final batch drain returns to later Updates |
| 9 | Instrumentation ON/OFF | Unavailable: first CPU OFF process blocked before launch. Two reversed OFF/ON pairs per arm and 2% investigation threshold preregistered |
| 10 | Final-binary correctness | 100k/25%, seed69501203, 1000-frame calibration: 10/10 counts, exact sets and non-black equivalent RGBA hashes pass; separate 96-frame diagnostic calibration/check also passes |
| 11 | Single CPU pilot | Not launched |
| 12 | Single GPU pilot | Not launched |
| 13 | CPU GPU-draw summary | No accepted pilot summary. Short availability-only distribution below |
| 14 | GPU cull/draw/range summaries | No accepted pilot summaries. Short availability-only distributions below |
| 15 | Batch completion pilot summaries | Unavailable; common boundary implemented and resolves in short diagnostics, but overhead/quality acceptance not reached |
| 16 | Pacing/background | No clean 1000-frame pacing acceptance. Short-run pacing check did not flag a concentration; overhead preflight failed 20/16/11% background load |
| 17 | Three-pair feasibility | Not reached |
| 18 | Correctness matrix | 1/18 primary conditions passed; remaining 17 deliberately not run |
| 19 | Tests/CI | Unity build, native MSVC /W4 /WX compile, managed build pass; 222 C# and 32 Python CI-suite tests pass locally. Hosted result tracked on PR #12 |
| 20 | Current PR head | Exact reviewed commit and CI link recorded in PR #12 to avoid a self-referential report SHA |
| 21 | Infrastructure merge readiness | No: overhead, clean CPU/GPU pilots, 1000/1000 acceptance, pacing, three pairs and 18/18 correctness remain pending |
| 22 | Evidence-size recommendation | Do not delete now. After stability, retain final accepted fixtures/manifests/protocols/reports in main; consider archiving superseded zero-sample probes with verified hashes and a durable retrieval location before deleting any files |

## Command-placement evidence

The first fixed RenderDoc frame 320 captured Unity's splash screen, not the workload. Its raw capture and extraction are preserved as a failed placement attempt. The corrected capture uses a longer **capture-only** warmup (GPU 50000 / CPU 2000) and triggers after startup. It uses the same final Player, same 100k population, seed, shader, procedural geometry and `Draw` implementation; this does not change the 300+1000 benchmark protocol. Capture processes and all their durations are excluded from performance evidence.

Verified GPU chunks: **168 EndQuery index69 (T0) -> 169 counter reset copy -> 179 Dispatch(391,1,1) -> 180 EndQuery index70 (T1) -> 183 CopyBufferRegion(dst offset4, 4 bytes) -> 197 ExecuteIndirect -> 198 EndQuery index71 (T2) -> 199 ResolveQueryData(start69,count3)**. Thus counter copy belongs to gpuDrawMs, not gpuCullMs.

Verified CPU chunks: **78 visible-ID upload copy -> 88/89 EndQuery indices30/31 (T0/T1) -> 98 DrawInstanced(6 vertices,24996 instances) -> 99 EndQuery index32 (T2) -> 100 ResolveQueryData(start30,count3)**. CPU upload's GPU copy is outside the draw-stage range, but within batch completion. Other Unity-owned frame-timing queries are separate from the native three-query heap and are not counted as benchmark triplets.

The structured extraction and automated placement checks are committed; four `.rdc` files remain local with paths/hashes in the manifest. The capture utility uses RenderDoc's scripting API, not GUI automation. Successful captures validate ordering, not timing accuracy or overhead.

## Short diagnostic distributions, not performance pilot results

These 96 measured frames use the real workload after 300 warmup frames, with a separately frozen 96-frame trajectory/calibration. They ran as availability/identity diagnostics on an active desktop. They are not substitutes for the clean 1000-frame pilot and cannot establish an architecture winner.

| Native GPU interval (ms) | Mean | Median | p10 | p90 | p95 |
|---|---:|---:|---:|---:|---:|
| CPU draw | .061707 | .061440 | .058368 | .065536 | .066560 |
| CPU range | .061707 | .061440 | .058368 | .065536 | .066560 |
| GPU cull | .007872 | .008192 | .007168 | .008192 | .008192 |
| GPU draw, including counter copy | .022571 | .022528 | .022528 | .022528 | .022784 |
| GPU range | .030443 | .030720 | .029696 | .030720 | .030720 |

CPU gpuCullMs is JSON **null** in every measured sample; its back-to-back timestamps happen to have equal ticks in this diagnostic. Native validation now correctly accepts T0<=T1<=T2 while rejecting zero/missing timestamps and invalid frequency/fences. GPU stage sums agree with range within floating-point tolerance. Full per-frame CPU submetrics, batch ticks, stage distributions and pacing summaries remain in the raw/summary JSON, without computing a CPU/GPU speedup.

## Boundary, safety and controls

Batch start is immediately before the first measured frame's CPU work in Draw. A separate warmup fence prevents unfinished warmup GPU work entering the batch. The final fence is placed after the final draw and timestamp resolve. Subsequent Updates poll without blocking or submitting more benchmark work. Last-false poll timestamps are taken before polling, first-true timestamps after polling; their gap is reported as observation uncertainty per frame. This metric is amortized completion/throughput, not individual-frame latency, and CPU and GPU submetrics are never simply added together.

ON and OFF initialize the same native backend and resources; ON inserts three timestamps per warmup and measured frame, OFF inserts none and does not poll query results. Every measured ON frame records submission/resolved ID, ring slot, raw T0/T1/T2, frequency, required/completed fences, Unity submission/availability frames and delay. Submission IDs include warmup; measured IDs are 301–396 in the short diagnostic. Native and managed checks prevent slot overwrite, duplicate IDs/resolution, bad order, invalid frequency/fences and averaging a surviving subset. The ring remains 32 slots; no waits, flushes or visible-count readbacks were added to timing.

[Protocol v4](../../unity/GpuDrivenCrowdBenchmark/PROTOCOL_V4.md) freezes overhead controls: two fresh reversed OFF/ON pairs for each arm, 300+1000 frames, 2% absolute paired-shift investigation threshold, no guessed subtraction. Report both shifts and their range, not a statistical confidence interval. The v2 pacing/GC/drift gates and three <=5% pre-run samples apply to all overhead and pilot processes. Final-fence observation uncertainty must be <=1% of batch cost. The runner stops on any failed prerequisite, preserves blocked receipts and disables the old legacy pilot entry point; it contains no formal matrix stage. Present-wait metrics are unavailable rather than zero-filled.

Final source identity: `7e8f184ccdd6a6dd0eef1398538f3a4e51602761f633e157d5e6887cca3a1474`. Native DLL hash: `cf0557d4bfc1ee96115c06915c9bc638113d12418447bc3c7ed734b3fab91526`. The same binary supplied correctness, real-workload short diagnostics and placement captures. Source/DLL build receipts and all 31 compact evidence files are tracked in the [manifest](../evidence/gpu-timing-integrated-v4-20260919/manifest.json). Calibration launch receipts from the first runner version retained a preflight status after successful output; subsequent correctness verifies those payloads and hashes. The early-return receipt update is fixed for future runs, without altering raw receipts.

New regression tests validate the accepted real CPU/GPU diagnostic fixtures, reject partial/stale/mis-slotted/fence-invalid/unit-invalid records, enforce CPU null, verify command order, and prove a blocked overhead launch never starts a Player. Older v1/v2/v3 files remain unchanged. Final accepted v4 fixtures also support these tests and should remain durable repository evidence.
