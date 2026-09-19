# Draw-ramp mechanism diagnostic — 2026-09-19

**Strongest supported classification: B — GPU clock/P-state associated execution-rate changes.** This is the leading explanation supported by the new diagnostics, not proof of the exact historical cause of the rejected ON ramp. The original run lacked state telemetry and remains rejected under v4. No acceptance, overhead or crossover result is produced.

Frozen workload is exactly constant, yet mean draw changes with a P0/high-clock to P5/lower-clock transition. Forward/reverse have identical view-specific pixel work but poorly matching draw durations. These findings contradict a simple explanation that the old near-doubling arose from more fragments along the view path. Forward also contains one unexplained large spike; interference cannot be ruled out or assigned to a process from aggregate utilization.

## Scope and implementation

Predeclared [diagnostic protocol](../../unity/DrawDriftDiagnostic/PROTOCOL.md): Stage A CPU 96 frames, then exactly **forward → frozen → reverse**, each 100k/.25, seed69501203, 300 warmup, 1000 measured diagnostic frames. Frozen uses calibrated **view[500]**. No recalibration. All four processes completed once, with no retries and no additional hardware runs.

A dedicated generated project/Player reuses the existing CPU all-agent visibility/list/upload/direct procedural draw, original population generator and predicate, six-vertex quad shader, stable depth, 1280x720 RGBA8/D32 target and D3D12. The actual `Draw` C# method is byte-identical in source to v4 (regression-tested). Diagnostic-only patches choose view indices, precompute geometry, add identity/time fields and consume pipeline stats. Formal source directory, runner, checker, 32-slot timestamp implementation and <=5% acceptance gate remain unchanged.

Stage A uses the inherited short-run warmup index modulo 96 (views0..95 cycled to 300 warmup frames); the three full runs use 0..299 warmup indices mapped through their trajectory, as declared. Stage A is instrumentation validation only. This clarifies the short-run indexing, not a change to the full-run ordering.

Native diagnostic plugin uses a separate **32-slot PIPELINE_STATISTICS heap/readback** sharing timestamp submission IDs and the same completion fence. T1 is recorded, pipeline BeginQuery surrounds only the direct draw, pipeline EndQuery occurs before T2, then both queries resolve; fence-complete readback returns the full 11-counter structure in the same call as the timestamp result before releasing the slot. No wait or timing-only flush. Original timestamp heap capacity and T0/T1/T2 call boundaries are retained in the isolated derivative. Pipeline queries themselves perturb the diagnostic range; do not compare its duration/batch time to old controls.

The [Microsoft counter definition](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_query_data_pipeline_statistics) defines vertex/pixel invocation and clipping counters. All 11 fields are preserved, including VSInvocations, CInvocations, CPrimitives and PSInvocations.

All run results, launch receipts, assessments, telemetry and derived outputs have `diagnosticOnly:true` and `eligibleForPerformanceAcceptance:false`. Player schema **104** fails the formal v4 checker (regression-tested). Generated original build receipts are wrapped with these diagnostic markers in evidence. The diagnostic runner has no formal stage or retry loop.

Local project `D:\CodexValidation\draw-drift-project`, Player `D:\CodexValidation\draw-drift-player\DrawDrift.exe`, native build `D:\CodexValidation\draw-drift-native`, raw runs `D:\CodexValidation\draw-drift-runs`. [Manifest](../evidence/draw-drift-diagnostic-20260919/manifest.json) retains compressed/uncompressed hashes, original/generated sources, build identities and calibration SHA. Original calibration and historical rejection files remain unchanged.

## Instrumentation validation

Stage A **96/96** timestamp/stat results, max ring **2**. Forward/Frozen/Reverse each **1000/1000**, max ring **3/2/3**. All frequency/ID/slot/tick/fence checks pass; no overwrite or missing result. Native frequency 1 GHz, MultiThreaded D3D12, profiler disabled. Pipeline counters resolve; VS=6×visible and CInvocations=CPrimitives=2×visible in **every recorded frame**, reported as observed sanity rather than a universal driver assertion.

NVML first observation precedes Player launch; telemetry continues after Player exit. The local supported-query listing was inspected before field implementation. [NVIDIA NVML read APIs](https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html) are called from one persistent ctypes sidecar per diagnostic, never from a Unity frame and never by spawning nvidia-smi every sample. Target cadence **100 ms**; GPU/SM/memory clocks, P-state, board power, utilization and temperature all available on this device. No process identity or personal process inventory collected. Board-power sensors may average over a longer period than the poll interval; neither power nor utilization is benchmark-specific.

| Run | Cadence mean ms | min | max | Query mean ms | Query max ms | Missed deadlines | Missing fields |
|---|---|---|---|---|---|---|---|
| short | 100.004733 | 99.264893 | 100.837646 | 0.077611 | 1.462800 | 0 | {} |
| forward | 100.001837 | 99.468018 | 100.565186 | 0.073452 | 1.390500 | 0 | {} |
| frozen | 100.003442 | 99.463623 | 100.536865 | 0.079161 | 1.642800 | 0 | {} |
| reverse | 100.002080 | 99.550781 | 100.549805 | 0.072930 | 1.452200 | 0 | {} |

Query latency is sidecar call time, not a measured impact on benchmark performance. Samples use UTC and monotonic timestamps. Unity records diagnostic start, CPU submission/resolve UTC per measured frame, first/last measured frame and finish; receipt records process launch/exit. Blocks use CPU submission windows (last block through final availability); nearest telemetry within 150 ms supplies approximate frame alignment. This is **not exact GPU execution time alignment**. With only 2–3 telemetry samples per block and internally averaged sensors, adjacent data are dependent. Block state associations are included to expose coarse resolution. No p-values or independence claim. FrameTimingManager is not used as primary timing; additional whole-frame/present metrics remain unavailable.

## Main findings

- Forward draw first/last quarter **119.656960 → 103.028352 us (-13.896900%)**. Its shape is a rise/plateau followed by a decline, not a reproduction of the original monotonic ramp. Graphics clock falls 270→210 then rises toward 300 MHz; memory remains405 MHz/P8. Frame675 has a retained **1363.968 us** spike; its cause is unknown. Mean and median blocks both reported, no trimming.
- Reverse **132.636416 → 133.843200 us (+0.909844%)**, graphics210 MHz, memory405 MHz/P8 throughout measured telemetry. Small view-linked variations exist, but not a near-doubling.
- Frozen **25.604480 → 33.915520 us (+32.459320%)**. Blocks0–699 stay about25.62 us, block700–799 begins increasing, final blocks about33.97/35.47 us. Sampled P0→P5 transition occurs **~2.347 s after first measured submission**, graphics2535→1065 MHz then930 MHz, memory10501→810 MHz. Temperature stays47 C. It is a late state-associated step, not the exact original ramp shape.
- Frozen draw vs graphics clock: frame-nearest Spearman **-0.716005**, R² **0.868884**; ten-block means Spearman **-0.700649**, R² **0.951198**. Forward block association is also inverse, Spearman **-0.875811**, R² **0.742778**. State and time are confounded; these are associations, not controlled clock interventions.

## Matched views and frozen constancy

Forward frame i and Reverse frame999-i have exactly matching **PSInvocations, visible counts, visible-ID hashes, projected/clipped area and partial-clipping counts for all 1000 view IDs**. Complete pairs are retained in analysis.json.gz. Matched draw Spearman **-0.067695**, R² **0.001784**: view identity alone is not a stable predictor of measured time across these different GPU states. This does not prove that draw work has zero view dependence.

Frozen: view500, visible **24817**, VS **148902**, CInvocations/CPrimitives **49634**, PS **133598** on every frame. Full projected area **144925.31757779734 pixels²** is constant; clipped area, summed world area, partial-clipping count and visible-ID hash are fixed precomputed properties of the same view. Pixel-area sums are continuous geometric areas before overlap/depth, not exact rasterization invocation counts. Clipped sum additionally intersects each orthographic quad with the view rectangle. Proxies are precomputed before warmup, outside measured work; no geometry-analysis pass was added to Draw.

Frozen PS/visible is constant; draw/PS first/last quarter **0.191653168 → 0.253862483 ns/invocation**. This is only normalized parallel draw duration, **not literal per-invocation shader latency**. Constant PS work with increasing time supports changing execution rate/state.

The trajectory is mildly nonstationary in work: forward PS first/last quarter **135981.732 → 133919.828 (-1.516310%)**, projected area **147893.452333 → 145735.872378 (-1.458875%)**; reverse contains the exact reversed values. This small deterministic work variation does not explain the original ~90% draw shift by a simple “more pixels” model. Same-view timings, frozen behavior and clock associations point more strongly to state.

## forward — 10 fixed blocks

| Frames | Draw mean us | Draw median us | Visible | PS | VS | CInvocations | CPrimitives | PS/visible | ns/PS (descriptive) |
|---|---|---|---|---|---|---|---|---|---|
| 0-99 | 116.774 | 117.760 | 25332.62 | 136369.90 | 151995.72 | 50665.24 | 50665.24 | 5.383174 | 0.856327 |
| 100-199 | 119.642 | 118.784 | 25255.97 | 135875.60 | 151535.82 | 50511.94 | 50511.94 | 5.379946 | 0.880551 |
| 200-299 | 128.812 | 127.632 | 25143.24 | 135356.28 | 150859.44 | 50286.48 | 50286.48 | 5.383409 | 0.951661 |
| 300-399 | 133.710 | 133.248 | 25061.14 | 134877.71 | 150366.84 | 50122.28 | 50122.28 | 5.381945 | 0.991350 |
| 400-499 | 133.754 | 133.248 | 24923.21 | 134086.40 | 149539.26 | 49846.42 | 49846.42 | 5.379982 | 0.997530 |
| 500-599 | 133.233 | 133.120 | 24844.44 | 133581.82 | 149066.64 | 49688.88 | 49688.88 | 5.376730 | 0.997386 |
| 600-699 | 145.701 | 133.120 | 24824.84 | 133508.54 | 148949.04 | 49649.68 | 49649.68 | 5.378023 | 1.091123 |
| 700-799 | 126.741 | 114.688 | 24807.46 | 133499.46 | 148844.76 | 49614.92 | 49614.92 | 5.381428 | 0.949390 |
| 800-899 | 100.184 | 98.304 | 24918.03 | 134100.03 | 149508.18 | 49836.06 | 49836.06 | 5.381648 | 0.747131 |
| 900-999 | 96.552 | 96.256 | 24896.49 | 133857.23 | 149378.94 | 49792.98 | 49792.98 | 5.376550 | 0.721308 |

| Frames | Graphics MHz | Memory MHz | Board W | GPU % | Temp C | P-state sample counts | Samples |
|---|---|---|---|---|---|---|---|
| 0-99 | 270.000 | 405.000 | 20.210 | 26.000 | 46.000 | {"8": 2} | 2 |
| 100-199 | 240.000 | 405.000 | 22.256 | 22.000 | 46.000 | {"8": 3} | 3 |
| 200-299 | 230.000 | 405.000 | 22.277 | 22.333 | 46.000 | {"8": 3} | 3 |
| 300-399 | 210.000 | 405.000 | 22.320 | 23.000 | 46.000 | {"8": 3} | 3 |
| 400-499 | 210.000 | 405.000 | 22.085 | 14.333 | 46.000 | {"8": 3} | 3 |
| 500-599 | 210.000 | 405.000 | 21.968 | 10.000 | 46.000 | {"8": 3} | 3 |
| 600-699 | 210.000 | 405.000 | 22.304 | 33.000 | 46.000 | {"8": 2} | 2 |
| 700-799 | 210.000 | 405.000 | 22.304 | 33.000 | 46.000 | {"8": 3} | 3 |
| 800-899 | 255.000 | 405.000 | 23.257 | 48.000 | 46.000 | {"8": 3} | 3 |
| 900-999 | 270.000 | 405.000 | 23.501 | 36.000 | 46.000 | {"8": 3} | 3 |

| Frames | Full projected pixel area | Clipped projected pixel area |
|---|---|---|
| 0-99 | 148302.442811 | 147458.759197 |
| 100-199 | 147806.969016 | 146984.140142 |
| 200-299 | 147125.171011 | 146283.253093 |
| 300-399 | 146559.402041 | 145740.638349 |
| 400-499 | 145599.264957 | 144765.566928 |
| 500-599 | 145152.107746 | 144334.056491 |
| 600-699 | 145114.695177 | 144270.934693 |
| 700-799 | 145143.688994 | 144336.074013 |
| 800-899 | 145904.039624 | 145095.819960 |
| 900-999 | 145712.058156 | 144867.222941 |


## frozen — 10 fixed blocks

| Frames | Draw mean us | Draw median us | Visible | PS | VS | CInvocations | CPrimitives | PS/visible | ns/PS (descriptive) |
|---|---|---|---|---|---|---|---|---|---|
| 0-99 | 25.621 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191775 |
| 100-199 | 25.614 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191723 |
| 200-299 | 25.629 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191838 |
| 300-399 | 25.634 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191876 |
| 400-499 | 25.644 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191945 |
| 500-599 | 25.621 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191780 |
| 600-699 | 25.623 | 25.600 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.191794 |
| 700-799 | 28.151 | 25.776 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.210717 |
| 800-899 | 33.969 | 33.792 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.254265 |
| 900-999 | 35.472 | 35.840 | 24817.00 | 133598.00 | 148902.00 | 49634.00 | 49634.00 | 5.383326 | 0.265515 |

| Frames | Graphics MHz | Memory MHz | Board W | GPU % | Temp C | P-state sample counts | Samples |
|---|---|---|---|---|---|---|---|
| 0-99 | 2535.000 | 10501.000 | 62.204 | 8.000 | 47.000 | {"0": 3} | 3 |
| 100-199 | 2535.000 | 10501.000 | 63.032 | 11.000 | 47.000 | {"0": 3} | 3 |
| 200-299 | 2535.000 | 10501.000 | 63.030 | 9.667 | 47.000 | {"0": 3} | 3 |
| 300-399 | 2535.000 | 10501.000 | 63.025 | 7.000 | 47.000 | {"0": 3} | 3 |
| 400-499 | 2535.000 | 10501.000 | 63.138 | 9.000 | 47.000 | {"0": 2} | 2 |
| 500-599 | 2535.000 | 10501.000 | 63.251 | 11.000 | 47.000 | {"0": 3} | 3 |
| 600-699 | 2535.000 | 10501.000 | 63.204 | 8.333 | 47.000 | {"0": 3} | 3 |
| 700-799 | 2535.000 | 10501.000 | 63.180 | 7.000 | 47.000 | {"0": 3} | 3 |
| 800-899 | 1065.000 | 810.000 | 61.746 | 16.000 | 47.000 | {"5": 3} | 3 |
| 900-999 | 1020.000 | 810.000 | 57.163 | 15.333 | 47.000 | {"5": 3} | 3 |

| Frames | Full projected pixel area | Clipped projected pixel area |
|---|---|---|
| 0-99 | 144925.317578 | 144165.674231 |
| 100-199 | 144925.317578 | 144165.674231 |
| 200-299 | 144925.317578 | 144165.674231 |
| 300-399 | 144925.317578 | 144165.674231 |
| 400-499 | 144925.317578 | 144165.674231 |
| 500-599 | 144925.317578 | 144165.674231 |
| 600-699 | 144925.317578 | 144165.674231 |
| 700-799 | 144925.317578 | 144165.674231 |
| 800-899 | 144925.317578 | 144165.674231 |
| 900-999 | 144925.317578 | 144165.674231 |


## reverse — 10 fixed blocks

| Frames | Draw mean us | Draw median us | Visible | PS | VS | CInvocations | CPrimitives | PS/visible | ns/PS (descriptive) |
|---|---|---|---|---|---|---|---|---|---|
| 0-99 | 132.636 | 132.256 | 24896.49 | 133857.23 | 149378.94 | 49792.98 | 49792.98 | 5.376550 | 0.990875 |
| 100-199 | 132.828 | 132.304 | 24918.03 | 134100.03 | 149508.18 | 49836.06 | 49836.06 | 5.381648 | 0.990521 |
| 200-299 | 131.711 | 131.328 | 24807.46 | 133499.46 | 148844.76 | 49614.92 | 49614.92 | 5.381428 | 0.986598 |
| 300-399 | 132.019 | 132.096 | 24824.84 | 133508.54 | 148949.04 | 49649.68 | 49649.68 | 5.378023 | 0.988845 |
| 400-499 | 132.087 | 132.096 | 24844.44 | 133581.82 | 149066.64 | 49688.88 | 49688.88 | 5.376730 | 0.988813 |
| 500-599 | 133.126 | 133.120 | 24923.21 | 134086.40 | 149539.26 | 49846.42 | 49846.42 | 5.379982 | 0.992836 |
| 600-699 | 133.686 | 133.120 | 25061.14 | 134877.71 | 150366.84 | 50122.28 | 50122.28 | 5.381945 | 0.991159 |
| 700-799 | 133.176 | 133.120 | 25143.24 | 135356.28 | 150859.44 | 50286.48 | 50286.48 | 5.383409 | 0.983896 |
| 800-899 | 133.778 | 134.144 | 25255.97 | 135875.60 | 151535.82 | 50511.94 | 50511.94 | 5.379946 | 0.984575 |
| 900-999 | 134.305 | 134.144 | 25332.62 | 136369.90 | 151995.72 | 50665.24 | 50665.24 | 5.383174 | 0.984857 |

| Frames | Graphics MHz | Memory MHz | Board W | GPU % | Temp C | P-state sample counts | Samples |
|---|---|---|---|---|---|---|---|
| 0-99 | 210.000 | 405.000 | 19.495 | 25.000 | 46.000 | {"8": 2} | 2 |
| 100-199 | 210.000 | 405.000 | 20.604 | 20.000 | 46.000 | {"8": 3} | 3 |
| 200-299 | 210.000 | 405.000 | 22.823 | 10.000 | 46.000 | {"8": 3} | 3 |
| 300-399 | 210.000 | 405.000 | 23.052 | 10.000 | 45.333 | {"8": 3} | 3 |
| 400-499 | 210.000 | 405.000 | 23.166 | 10.000 | 45.000 | {"8": 3} | 3 |
| 500-599 | 210.000 | 405.000 | 23.323 | 10.000 | 45.000 | {"8": 2} | 2 |
| 600-699 | 210.000 | 405.000 | 23.323 | 10.000 | 45.000 | {"8": 3} | 3 |
| 700-799 | 210.000 | 405.000 | 23.320 | 10.000 | 45.000 | {"8": 3} | 3 |
| 800-899 | 210.000 | 405.000 | 23.344 | 10.000 | 45.000 | {"8": 3} | 3 |
| 900-999 | 210.000 | 405.000 | 23.393 | 10.000 | 45.000 | {"8": 3} | 3 |

| Frames | Full projected pixel area | Clipped projected pixel area |
|---|---|---|
| 0-99 | 145712.058156 | 144867.222941 |
| 100-199 | 145904.039624 | 145095.819960 |
| 200-299 | 145143.688994 | 144336.074013 |
| 300-399 | 145114.695177 | 144270.934693 |
| 400-499 | 145152.107746 | 144334.056491 |
| 500-599 | 145599.264957 | 144765.566928 |
| 600-699 | 146559.402041 | 145740.638349 |
| 700-799 | 147125.171011 | 146283.253093 |
| 800-899 | 147806.969016 | 146984.140142 |
| 900-999 | 148302.442811 | 147458.759197 |

## Descriptive associations

Constant Frozen work and constant Reverse clocks yield **undefined** correlations (reported unavailable, not zero). Frame-nearest telemetry repeats sensor values; n below counts frames, not independent state observations.

| Trajectory | Draw vs | Aligned frames | Spearman | OLS R2 |
|---|---|---|---|---|
| forward | frameIndex | 1000 | -0.267972 | 0.012839 |
| forward | PSInvocations | 1000 | -0.205782 | 0.001884 |
| forward | visibleCount | 1000 | -0.228468 | 0.003070 |
| forward | projectedArea | 1000 | -0.328756 | 0.005840 |
| forward | clippedArea | 1000 | -0.329880 | 0.005916 |
| forward | graphicsMHz | 1000 | -0.795250 | 0.072392 |
| forward | powerMilliwatts | 1000 | -0.339962 | 0.015356 |
| frozen | frameIndex | 1000 | 0.574883 | 0.544028 |
| frozen | PSInvocations | 1000 | unavailable | unavailable |
| frozen | visibleCount | 1000 | unavailable | unavailable |
| frozen | projectedArea | 1000 | unavailable | unavailable |
| frozen | clippedArea | 1000 | unavailable | unavailable |
| frozen | graphicsMHz | 1000 | -0.716005 | 0.868884 |
| frozen | powerMilliwatts | 1000 | -0.486126 | 0.289201 |
| reverse | frameIndex | 1000 | 0.454018 | 0.132083 |
| reverse | PSInvocations | 1000 | 0.542706 | 0.189639 |
| reverse | visibleCount | 1000 | 0.564153 | 0.190720 |
| reverse | projectedArea | 1000 | 0.555241 | 0.188810 |
| reverse | clippedArea | 1000 | 0.552556 | 0.187005 |
| reverse | graphicsMHz | 1000 | unavailable | unavailable |
| reverse | powerMilliwatts | 1000 | 0.434415 | 0.020246 |

Full derived evidence also retains graphics/memory/power/utilization/temperature **mean/min/max per block**, P-state distributions, per-frame ns/PS and PS/visible derivations, and all matched-view rows. The table above shows means for readability.

## State, background and warmup interpretation

Measured state ranges:

| Run | Graphics MHz | Memory MHz | P-state | Board W | GPU utilization | Temperature |
|---|---|---|---|---|---|---|
| Forward | 210–300 | 405 | P8 | 20.210–23.988 | 10–48% | 46 C |
| Frozen | 930–2535 | 810–10501 | P0 then P5 | 47.996–63.251 | 7–16% | 47 C |
| Reverse | 210 | 405 | P8 | 19.495–23.393 | 10–25% | 45–46 C |

Prelaunch utilization ranges were StageA6–13%, Forward2–10%, Frozen4–6%, Reverse18–30%; postexit2%,12%,14%,6% respectively. These are diagnostic observations, not a quiet gate or accepted benchmark. Load was not uniformly quiet. Aggregate utilization mixes our work and any other work, and utilization also depends on state; it cannot identify external contributors. No external process is confidently implicated. The forward spike remains unexplained. Class D would require stronger independent evidence that multiple mechanisms materially explain the phenomenon, so it is not forced here.

The strongest supported answer is **B**, qualified as GPU clock/P-state-associated behavior in these instrumented runs. One process per trajectory, different starting states, aggregate sensors, telemetry resolution and added stats instrumentation limit causal attribution and generalization to the earlier uninstrumented state history. Graphics and memory clocks change together in Frozen, so their individual contributions cannot be separated.

**300 warmup frames did not guarantee stable GPU state.** Frozen changes state about2.347 seconds into measurement despite fixed workload and warmup. Forward clocks also change during measured work. Reverse happens to stay in P8. Four diagnostics cannot establish a repeatable settling duration or a sufficient fixed warmup length. Warmup through CPU-bound low-duty GPU work can coexist with continuing state transitions; no driver setting was changed.

## Prospective recommendation and unchanged acceptance

Do not resume acceptance yet. Separately preregister a small state-stabilization investigation: fixed time-based warmup candidates under the actual workload, continued clock/P-state observation through the full measurement interval, defined stability criteria and predetermined orders/attempt limits. Do not trim early frames after observing a trace or lock clocks to favor an arm. Determine whether the same workload reaches a repeatable state before selecting a new warmup policy. This recommendation is not implemented as a formal protocol change.

View workload varies modestly; future formal analysis should acknowledge paired identical trajectories/view-index matching if necessary, but these data do not justify redesigning view order or weakening v4 drift now. The old v4 rejection stands. No instrumentation acceptance, pilot, feasibility pairs, correctness expansion, matrix, merge or evidence cleanup occurred.

## Validation and delivery

Native isolated MSVC /O2 /W4 /WX build and Unity6000.3.13f1 Development build succeeded; graphicsJobs=false, autoconnect=false. **53 Python tests pass locally**, including eight new tests for exact actual-Draw preservation, diagnostic query boundaries/fence ordering, patch failure on source drift, real four-run integrity, formal-checker rejection, corrupted IDs/stats/units, matched view/frozen identity and time-alignment bounds. Hosted CI runs these tests; final-head status and SHA are reported after push. Hosted CI does not rerun GPU diagnostics. PR #12 stays Draft and unmerged.
