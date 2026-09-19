# GPU-state stability diagnostic — 2026-09-19

**Blocked before first Player. 0 valid diagnostics, 1 blocked preflight, 5 positions not attempted. Recommendation C: unresolved because the required new data do not exist.** No duration/margin or V5 proposal is justified. Performance acceptance remains stopped; PR #12 remains Draft.

All evidence is diagnosticOnly=true, eligibleForPerformanceAcceptance=false. [Prospective protocol](../../unity/GpuStateStabilityDiagnostic/PROTOCOL.md) was written before the attempt. [Evidence manifest](../evidence/state-stability-diagnostic-20260919/manifest.json) preserves build provenance and the original blocked receipt. Base head: `9b572131cefc3b6de8d763469c83cb5fd40bf627`.

## Implementation and validation boundary

The isolated generator preserves the real all-agent CPU scan/list/upload/direct draw and GPU cull/append/counter-copy/indirect draw, original population, shader, target, calibration and timestamp DLL. Generated schema105 records cyclic view identity, every submission from frame0, frame300, deferred native timestamps and a30-second wall-clock stream. It preallocates up to2,000,000 samples; memory/runtime behavior remains unvalidated on hardware. The100ms NVML sidecar and fixed six-position runner stop on any nonquiet preflight. No pipeline-statistics queries, synchronous count readback, cycle waits or synthetic heater were added.

Median matched-view draw ratio must be0.95–1.05 with p10>=0.90 and p90<=1.10. Three consecutive stable transitions require four full cycles. Candidate boundary, later confirmation, relapse and sustained candidate are distinct; incomplete final cycles are retained but excluded. Clocks and P-state total variation are explanatory, never an equal-state requirement between arms.

Original V4 source, native DLL/ring/placement, runner/checker/gates and previous evidence are unchanged. Original source identity remains `7e8f184ccdd6a6dd0eef1398538f3a4e51602761f633e157d5e6887cca3a1474`. DLL SHA256 remains `cf0557d4bfc1ee96115c06915c9bc638113d12418447bc3c7ed734b3fab91526`.

Both isolated Unity6000.3.13f1 Development builds succeeded, Graphics Jobs off. The blocked attempt references the first build. After STOP, offline hardening corrected diagnostic per-frame denominator and unused failure-array serialization, followed by a second successful build. Neither build was launched. Build receipts and exact compressed generated sources distinguish both versions; the final build does not replace the blocked receipt's identity.

64 Python tests passed locally, including11 new tests for cyclic identities, ratios/tails, settling/relapse, incomplete cycles, invalid timings, telemetry boundaries, P-state TV, passive fence boundaries, no launch on blocked gate, generated real-arm commands, and a synthetic4500-frame end-to-end analysis. CI includes the new suite. Compilation and synthetic tests do not establish Player runtime validity.

## Six-position outcome

| Position | Arm process | Run ID | Quiet preflight | Frames/cycles | Settling seconds/cycle | Frame300 seconds |
|---|---|---|---|---|---|---|
|1|CPU1|e7370163-d576-4f99-8e96-d6cf5c4d328a|BLOCKED:34/34/35%|not launched|unavailable|unavailable|
|2|GPU1|not assigned|not attempted|not launched|unavailable|unavailable|
|3|GPU2|not assigned|not attempted|not launched|unavailable|unavailable|
|4|CPU2|not assigned|not attempted|not launched|unavailable|unavailable|
|5|CPU3|not assigned|not attempted|not launched|unavailable|unavailable|
|6|GPU3|not assigned|not attempted|not launched|unavailable|unavailable|

The RTX4090/driver591.86 preflight temperatures were44/44/44C. These three samples are the only new GPU observations. No Player, NVML sidecar or timing capture started. No automatic retry, substitution, quiet-window observer or later hardware attempt occurred.

CPU and GPU min/median/max settling, global worst, frame300-before-settling frequency, matched-cycle timings, graphics/memory clocks, P-state distribution, power and workload utilization/temperature are **unavailable**, not zero. No claim that a run failed to settle within30seconds is possible: none ran.

## Warmup boundary and trajectory reset: offline findings

Formal V4 executes warmup indices-300..-1 using views0..299. It inserts a fence after the last warmup submission. At nextFrame0 it returns while the fence is incomplete; when first observed complete it resets lastUpdate and returns once more. Measurement begins with view0 on the following Update. Thus the code permits a drain interval plus an extra Update interval. Historical output does not serialize the required timestamps, so exact drain/idle duration and materiality cannot be recovered.

The isolated diagnostic instead passively observes a fence after frame299 while continuing submissions. Its drain value would be an observation upper bound; a negative frame300-minus-observation delta makes idleGap null, not zero. This probe cannot measure the formal blocking transition. This attempt produced no boundary measurements at all.

Offline historical geometry from `draw-drift-diagnostic-20260919/forward.json.gz` shows the formal reset view299→view0 changes visible count25120→25362 (+0.963%), projected geometry147086.0043→148376.8194 (+0.878%), and PS invocations135072→136725 (+1.224%). View300 would have25123 visible and147094.3846 projected geometry. These are previous diagnostic observations, not new settling evidence. The input change is measurable; its effect on timing/state at the actual boundary is unresolved. A future protocol should examine continuous workload and avoid unnecessary idle while preserving the preregistered measured view sequence, but no redesign is implemented here.

## Decision

The previous finding that300 frames did not guarantee stability remains; this blocked attempt neither establishes a replacement nor supplies new evidence supporting300 frames. Choose **C, unresolved due to missing six-process data**, not a claim of observed nonsettling. Fixed wall-clock warmup using each arm's own workload remains a hypothesis. No duration or safety margin can be derived; no proposed V5 is justified. The previous rejected V4 ON process stays rejected. Acceptance must not resume on this result.

No overhead controls, acceptance pilots, feasibility pairs, expanded correctness, formal216-process study, driver/power/service changes, merge, evidence cleanup or infrastructure extraction were performed. Further diagnostic execution requires human intervention after the blocked gate; this delivery does not automatically retry.
