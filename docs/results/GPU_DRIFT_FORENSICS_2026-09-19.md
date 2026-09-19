# GPU drift forensic decision — 2026-09-19

**Retain v4; no Protocol v5; no hardware retry.** The rejected CPU ON shows a sustained draw-duration ramp, not stationary noise amplified by a small denominator. Its 55.357440 us shift is 1.825778988% of batch/frame and 2.058693400% of mean CPU total: small in absolute time, but not negligible for a percent-scale engineering investigation. This does **not** establish an equal causal throughput loss: CPU/GPU overlap and absence of per-block GPU-complete boundaries prevent that inference. CPU total and update intervals do not show a matching upward trend.

All three candidate policies below still flag this run. The scale issue is real for a near-zero empty boundary, but that boundary is not responsible for the GPU range failure. The process stays **REJECTED UNDER V4**. No instrumentation-overhead or crossover conclusion is made.

## Exact evidence and verification

Actual local directory: `D:\CodexValidation\hlsl-crossover-quiet-window-20260919`.

- Raw: `overhead-cpu-0-on.json`.
- Receipt: `overhead-cpu-0-on.launch.json`, exact rejection **`failed: GPU drift`**.
- Summary: `overhead-cpu-0-on.diagnostic-summary.json`, `qualityAccepted:false`, `failure:GPU drift`.
- Committed [raw gzip](../evidence/gpu-quiet-window-20260919/overhead-cpu-0-on.json.gz), [receipt](../evidence/gpu-quiet-window-20260919/overhead-cpu-0-on.launch.json), [summary](../evidence/gpu-quiet-window-20260919/overhead-cpu-0-on.diagnostic-summary.json).

Local raw bytes equal decompressed committed bytes. Raw SHA-256 `b31c3d83f4ed3f09f70843f5979faaa124d9d3cee235e3de55ca182f10ca8441`; source `7e8f184ccdd6a6dd0eef1398538f3a4e51602761f633e157d5e6887cca3a1474`; DLL `cf0557d4bfc1ee96115c06915c9bc638113d12418447bc3c7ed734b3fab91526`. Executable identity remains in the [prior quality manifest](../evidence/gpu-timing-quality-20260919/manifest.json). Baseline head `da650468f3d175ae061ed73538d0ca5fd0eb690c`. No binary rebuilt.

Verified before analysis: **1000 samples, 1000 submitted / 1000 resolved, invalid=0, ring max=3**. Existing evidence, receipt, summary, status and hashes were not modified. New derived JSON is separate from those files.

## Exact quarter calculation

Calls the existing `analyze_crossover.drift()`: `q=max(1,len(xs)//4)` (250 here), arithmetic first/last-quarter means, `abs(last/first-1)` with its existing zero behavior. Signed change and absolute relative change are distinguished.

| Metric | First quarter ms | Last quarter ms | Signed shift ms | Absolute relative drift |
|---|---|---|---|---|
| gpuRangeMs | 0.061509632 | 0.116867072 | +0.055357440 | 89.998002264% |
| gpuDrawMs | 0.061509632 | 0.116858880 | +0.055349248 | 89.984684025% |
| cpuTotalMs | 2.677494800 | 2.666873200 | -0.010621600 | 0.396699183% |
| cpuCullAndListMs | 2.610760800 | 2.609387200 | -0.001373600 | 0.052613016% |
| cpuUploadMs | 0.021544800 | 0.019573200 | -0.001971600 | 9.151164086% |
| cpuSubmitMs | 0.045189200 | 0.037912800 | -0.007276400 | 16.102077488% |
| updateIntervalMs | 2.999196400 | 2.964307600 | -0.034888800 | 1.163271602% |

GPU range **0.061509632 → 0.116867072 ms**, or **61.509632 → 116.867072 us**. Shift **0.055357440 ms = 55.357440 us**, relative **89.998002264%**. Draw shift **55.349248 us**, relative **89.984684025%**, accounts for 99.9852% of range shift. CPU submit's relative decline exceeds 15% but its 7.2764 us change is a submetric; v4's primary CPU gate uses cpuTotalMs.

## Ten fixed sequential blocks

| Frames | Range mean ms | Range median ms | Draw mean ms | CPU total mean ms | Update mean ms | Visible mean |
|---|---|---|---|---|---|---|
| 0-99 | 0.055910400 | 0.057344000 | 0.055910400 | 2.665968000 | 2.958930000 | 25332.62 |
| 100-199 | 0.063662080 | 0.062464000 | 0.063662080 | 2.675543000 | 2.993998000 | 25255.97 |
| 200-299 | 0.070809600 | 0.072192000 | 0.070809600 | 2.710874000 | 3.112356000 | 25143.24 |
| 300-399 | 0.078356480 | 0.076800000 | 0.078346240 | 2.705206000 | 3.096765000 | 25061.14 |
| 400-499 | 0.086179840 | 0.087040000 | 0.086169600 | 2.702330000 | 3.083021000 | 24923.21 |
| 500-599 | 0.098682880 | 0.097280000 | 0.098682880 | 2.680355000 | 3.019017000 | 24844.44 |
| 600-699 | 0.107171840 | 0.107520000 | 0.107171840 | 2.712955000 | 3.060696000 | 24824.84 |
| 700-799 | 0.116162560 | 0.115712000 | 0.116152320 | 2.712320000 | 3.066367000 | 24807.46 |
| 800-899 | 0.117278720 | 0.116736000 | 0.117278720 | 2.664502000 | 2.974329000 | 24918.03 |
| 900-999 | 0.116582400 | 0.116736000 | 0.116572160 | 2.659546000 | 2.928255000 | 24896.49 |

Range means and medians rise together through block 700–799 and plateau around 117 us. Neither isolated spikes nor stationary ratio noise explains this. Early/late quarters are ends of a broad ramp, not isolated anomalous quarters. Visible count generally falls while GPU time rises, but counts alone do not measure clipping, pixel coverage or overlap under the deterministic moving view. Cause is not identified.

## Whole-process distributions

All **1000** observations included; no outliers removed. The first update interval is 0.4175 ms. Previous pacing acceptance used frames 1–999; the all-frame forensic scope below explains the different minimum/mean, and does not change that gate. Percentiles use the existing interpolated implementation; stddev is population stddev; CV is a fraction.

| Statistic | GPU range ms | GPU draw ms | CPU total ms | Update interval ms |
|---|---|---|---|---|
| min | 0.052224000000 | 0.052224000000 | 2.512800000000 | 0.417500000000 |
| p10 | 0.058368000000 | 0.058368000000 | 2.606790000000 | 2.868790000000 |
| median | 0.095232000000 | 0.095232000000 | 2.674800000000 | 3.011800000000 |
| mean | 0.091079680000 | 0.091075584000 | 2.688959900000 | 3.029373400000 |
| p90 | 0.116736000000 | 0.116736000000 | 2.784250000000 | 3.199900000000 |
| p95 | 0.117760000000 | 0.117760000000 | 2.828890000000 | 3.282025000000 |
| p99 | 0.119808000000 | 0.119808000000 | 2.951004000000 | 3.422005000000 |
| max | 0.120832000000 | 0.120832000000 | 3.058400000000 | 5.706800000000 |
| stddev | 0.022226744421 | 0.022225459868 | 0.075242029624 | 0.188943590980 |
| CV | 0.244036259476 | 0.244033130411 | 0.027981834026 | 0.062370518926 |

Range in us: min **52.224**, p10 **58.368**, median **95.232**, mean **91.079680**, p90 **116.736**, p95 **117.760**, p99 **119.808**, max **120.832**, stddev **22.226744421**. Draw mean **91.075584 us**, stddev **22.225459868 us**.

## Trend diagnostics

OLS and tied-rank Spearman are descriptive; no frame-independence assumption, confidence interval or p-value.

| Metric | OLS slope ms/frame | Spearman vs frame | First half ms | Second half ms |
|---|---|---|---|---|
| gpuRangeMs | 0.000075396497 | 0.974676786 | 0.070983680 | 0.111175680 |
| gpuDrawMs | 0.000075391361 | 0.974745178 | 0.070979584 | 0.111171584 |
| cpuTotalMs | -0.000008973073 | -0.042970991 | 2.691984200 | 2.685935600 |
| cpuCullAndListMs | 0.000005013259 | 0.008397125 | 2.618009000 | 2.620426800 |
| cpuUploadMs | -0.000003063109 | -0.138539593 | 0.022584800 | 0.020752200 |
| cpuSubmitMs | -0.000010923223 | -0.167280349 | 0.051390400 | 0.044756600 |
| updateIntervalMs | -0.000052888960 | -0.106727798 | 3.049014000 | 3.009732800 |

Range slope **0.075396497 us/frame**, OLS R² **0.958890345**, Spearman **0.974676786**. First/second half **70.983680 / 111.175680 us**. The line summarizes a ramp plus late plateau; it does not imply unlimited linear growth. CPU total OLS R² **0.001185168**, rank **-0.042970991**: no comparable increasing primary trend.

## Architecture-scale comparison

`0.055357440 / 3.0319902` = **0.018257789883357804**, or **1.825778988% of batch/frame**.

`0.055357440 / 2.6889599` = **0.02058693400373877**, or **2.058693400% of mean CPU total**.

These are scale comparisons, not additive decomposition or a measured wall-clock penalty. An aggregate batch scalar cannot reveal within-run GPU-complete throughput drift. Update intervals are pacing/host observations, not per-frame GPU latency. The change is tens of microseconds, but large enough relative to the intended percent-scale engineering sensitivity that dismissing it needs evidence. Primary architecture instability remains unproven.

## Empty-boundary decomposition

Gap is computed as integer `(T1-T0)` then converted, avoiding subtraction of rounded durations. All CPU gpuCullMs fields are null.

- Mean **0.000004096 ms = 0.004096 us**; median/p95 **0**; max **0.001024 ms = 1.024 us**; 4/1000 nonzero samples.
- First-quarter gap **0**, last-quarter **0.000008192 ms = 0.008192 us**; relative drift infinite due to zero denominator. JSON uses null plus explicit infinite status, not nonstandard Infinity.
- Gap contributes only **0.0148%** of the range shift. This is an actual small/zero-denominator pathology in a diagnostic gap, but not the source of the draw-range rejection.

## Timestamp integrity

Unique frequency **[1000000000] Hz**. Submitted/resolved IDs exactly **301…1300**, duplicates **0**. Wrong slots **0**; T0<=T1<=T2 failures **0**; fence failures **0**; frequency mismatches **0**; cross-submission backward ticks **0**; duplicate raw triplets **0**. Existing units, ID and fence checks pass. These records contain no evidence of stale/misassigned resolution or wrap; this is internal consistency, not independent GPU remeasurement.

1 GHz gives a nominal 1 ns/tick conversion, not a demonstrated effective resolution. Observed range-delta GCD is **1024 ticks = 1.024 us**. A 55.357440 us mean shift is well above this empirical lattice. The lattice does not identify hardware versus serialization granularity.

Pre-run 0/0/0% cannot prove ongoing stability; post-run 9% cannot distinguish benchmark/residual utilization-window effects from unrelated load. Attribution is **unknown**. No per-frame clock/power/background trace exists to establish a cause.

## Purpose and limitations of drift gates

A within-process gate prevents averaging different performance regimes as steady state. Primary protection includes CPU total changes, pacing changes, batch/fence validity, GC, and background/thermal conditions. A GPU diagnostic trend can invalidate explanatory timing even when CPU dominates throughput. Conversely, large relative changes in almost-empty stages need scale context before being called architecture instability.

First/last-quarter checks can miss middle-only disturbances and legitimate deterministic trajectory costs. Block/trend reporting should accompany any scalar rule. Primary batch throughput is one process scalar; comparisons of GPU stage change against it do not replace a proper longitudinal architecture metric. Do not dismiss a real draw-time trend merely because the CPU-primary path is stable.

## Three candidate policies, evaluated before decision

All retain CPU-total >15% relative drift, pacing, GC, completion/fence, IDs/ring, thermal and <=5% formal pre-launch requirements. No candidate is installed in the validator.

| Candidate | GPU criterion | Rationale and limitation |
|---|---|---|
| A: relative AND absolute | Fail when relative >15% AND absolute shift >0.01 ms | A 10 us floor corresponds to 1% of an illustrative 1 ms architecture budget, not a calibrated timer-noise floor. It can miss important changes on fast GPU arms and overreact on slow arms. |
| B: relative AND architecture materiality | Fail when relative >15% AND shift / batch/frame >1% | Retains the old relative alert and adds sensitivity at half the existing ~2% engineering investigation scale. The 1% is a proposed error budget, not a statistical guarantee. A large batch denominator or a material change below 15% can mask problems. |
| C: primary/diagnostic hierarchy | Flag relative >15%; fail GPU stage whenever shift / batch/frame >1%, regardless of relative | Separates relative flags from scale significance and catches B's low-relative blind spot. Still needs prospective calibration and careful interpretation of overlap and measured denominators. |

15% is inherited, not increased. 1% is an explicitly hypothetical sensitivity budget linked to existing percent-level goals, not an already-preregistered drift rule. 0.01 ms illustrates fixed-floor dependence on architecture scale; nominal timestamp resolution does not justify that floor by itself. None was chosen because it rescues this run. Choosing a new limit above 1.8258% simply to waive this observation would be circular.

Equality does not trigger these exploratory `>` predicates. Zero→zero has relative drift 0; zero→positive is unbounded relatively, with finite absolute/architecture diagnostics. Malformed/nonfinite inputs fail. No tiny denominator epsilon is invented.

## Independent evidence sensitivity

| Evidence | N | GPU relative | Shift us | Batch fraction | A | B | C |
|---|---|---|---|---|---|---|---|
| v4_cpu96 | 96 | 7.341411% | 4.394666667 | 0.157290% | no flag | no flag | no flag |
| v4_gpu96 | 96 | 0.280505% | 0.085333333 | 0.073842% | no flag | no flag | no flag |
| v4_cpu_off1000 | 1000 | N/A | N/A | N/A | N/A | N/A | N/A |
| v4_cpu_on1000_rejected | 1000 | 89.998002% | 55.357440000 | 1.825779% | flag | flag | flag |
| v3_native_normal | 96 | 9.657321% | 3.968000000 | N/A | no flag | N/A | N/A |
| v3_native_batch | 96 | 4.100228% | 1.536000000 | N/A | no flag | N/A | N/A |

These are **criterion outcomes, not acceptance decisions**. The 96-frame v4 records are nonquiet short integration diagnostics. GPU96 primary CPU drift is **25.265554%**, only **0.002775 ms**; retaining the primary CPU rule would flag it even though its GPU stage is stable. Its original diagnostic status is unchanged. This independently illustrates that relative-only CPU submission cost may also be scale-sensitive on a fast GPU arm; changing only the stage which failed this run would be inconsistent.

OFF has no GPU timestamps and cannot calibrate GPU noise; its CPU drift is **0.289823%**. V3 records vary diagnostic repetitions **1–4**, have different implementation/scope and lack the primary batch metric. Their full-sequence arithmetic is context only; B/C are unavailable. They cannot validate a final-binary scale-aware threshold and are not normalized or pooled. Existing independent evidence is insufficient to set a universal noise floor or promote old runs.

## Selected policy and v5 decision

**Keep operational v4 and add offline structured diagnostics. Do not deploy A/B/C now.** C is the strongest future design candidate because it expresses the hierarchy and avoids B's low-relative blind spot, but it remains a proposal. This record shows genuine draw-time nonstationarity; all three candidates flag it. Independent evidence is too short, nonquiet or different-scope to validate a replacement. No gate pathology that changes the decision for this ON run has been demonstrated.

**No PROTOCOL_V5.md is created.** No change to `check_integrated_crossover.py`, native timestamps, runner, workload or <=5% gate. No v5 checker exists to apply to old evidence. The offline candidate outputs contain relativeDrift, absoluteDriftMs, architectureFraction, criterion booleans and diagnostic-only markers. The rejected ON remains REJECTED UNDER V4 and cannot become an accepted control.

## Reproduction, tests and next action

Stdlib-only `tools/analyze_crossover_drift.py` reads committed evidence and never launches hardware or subprocesses:

```powershell
python tools/analyze_crossover_drift.py --output <new-output.json>
```

Derived [JSON](data/gpu-drift-forensics-20260919.json) contains full-precision statistics, all tables, integrity checks, candidate outputs and input hashes. Eight synthetic tests cover 0.030→0.060 ms, 0.30→0.60 ms, stable stages, material primary CPU drift, zero/near-zero denominators, malformed/nonfinite values, low-relative material change and rank-trend behavior. They test candidate mathematics without encoding this failed run as an exception. Existing real-data regression still rejects it under v4. **45 Python tests pass locally**; final-head CI is checked after push and reported with the delivery SHA.

**A further quiet-window acceptance retry is not yet justified by this analysis.** A separately authorized diagnostic could collect fixed-cadence GPU clock/power and deterministic pixel-work/view proxies through warmup and measurement, to distinguish changing effective draw work from GPU state/interference. Do not change driver policy, trim samples after seeing results, or retry until a pass. No hardware process or quiet observer was started here. PR #12 remains Draft; no cleanup, merge, extraction or formal study.
