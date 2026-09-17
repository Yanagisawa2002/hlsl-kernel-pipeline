# RTX 4090 inclusive scan: hardware-profile diagnosis

Status: **timing-confirmed; runtime counters captured at whole-capture scope; scan-isolated counter attribution remains pending**.

The frozen September 15 result is unchanged: at `2^28` uint32 elements, the previous exclusive-scan-plus-conversion path took **6.376 ms**, the native-inclusive fused path took **2.904 ms**, and pinned GPUPrefixSums RTS took **3.584 ms**. The fused path reduced complete GPU operation time by **54.45%** versus the old path and used **18.97% less time** than RTS on that exact RTX 4090 workload.

Source accounting identified the candidate mechanism before profiling: the old path performs a separate full-array `AddInput` conversion after scan. At `2^28`, that stage logically reads input and output and rewrites output, or **3 GiB of full-array traffic**. The fused path emits inclusive values while the vector is already loaded.

A September 17 Nsight Graphics capture now adds runtime evidence. It supports the pass-elimination / reduced-memory-work hypothesis, but it does **not** convert the 3 GiB source accounting into a measured DRAM-byte claim because the captured region spans multiple submits and includes device-wide background activity.

## Performance question

**Did the 6.376 -> 2.904 ms improvement primarily come from eliminating the full-array conversion stage and associated memory-system work, or from improved shader execution efficiency?**

The current evidence favors the first explanation. It also falsifies a tempting alternative: the fused path did not win by obviously improving occupancy/register pressure.

## Frozen arms

| Arm | Meaning | Confirmed complete GPU operation |
|---|---|---:|
| `tile` | original exclusive scan + AddInput conversion | 6.376 ms |
| `tile-fused` | native inclusive output in the local GPUPrefixSums-derived adaptation | **2.904 ms** |
| `rts` | pinned GPUPrefixSums ReduceThenScan | 3.584 ms |

## Capture identity

The September 17 delivery used:

- NVIDIA GeForce RTX 4090, Wave32;
- driver 591.86 (`32.0.15.9186`);
- Nsight Graphics CLI `2026.3.1.0` build `38722833`;
- source commit `0b2e224928a344b8e5724aec1a50a8cedbc801f2`;
- `268,435,456` uint32 values, unchanged all-one inclusive-scan contract;
- one excluded warmup plus 100 timing iterations in the native harness;
- full-size validation passed for all three unprofiled sanity-check arms.

The original local archive SHA-256 is `61a5113f093c3164d21187572bfab7bfa029fdc62c8cee1a44e56a2fc665a225`.

Compact public evidence: [`../evidence/rtx4090-scan-nsight-20260917/README.md`](../evidence/rtx4090-scan-nsight-20260917/README.md) and [`summary.csv`](../evidence/rtx4090-scan-nsight-20260917/summary.csv).

## Capture scope and validity

GPU Trace is the correct class of tool for this diagnosis: it captures GPU-unit utilization, timeline events, occupancy/throughput signals and shader-profiler data. NVIDIA also documents that GPU Trace collects **all GPU activity**, so background activity must be treated as a validity concern.

The delivered throughput/shader traces start after 20 submits and are limited to 6 submits. The inherited loop submits input generation, scan work and query resolve separately. The exported `GPUTRACE_REGIMES` files contain no user-marker rows.

Therefore:

- the exported counters below are **whole-capture aggregates**, not scan-only counters;
- the capture averages must not be compared directly with the frozen single-operation timings;
- activity percentages are not DRAM byte counts;
- active-compute-warp and register-allocation rows are occupancy/pressure signals, not a scan-kernel achieved-occupancy proof;
- real-time shader PCSampler percentages retain Nsight's original denominators and are not a normalized stall pie chart.

Three attempted multi-pass captures were retained, but Nsight explicitly reported that multi-pass metrics are unsupported for the submit-limited trace end condition. They are not used as multi-pass evidence.

References:

- NVIDIA Nsight Graphics GPU Trace overview: <https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-overview.html>
- NVIDIA GPU Trace UI / metrics reference: <https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-ui.html>
- NVIDIA Shader Profiler: <https://docs.nvidia.com/nsight-graphics/UserGuide/shader-profiler.html>

## Unprofiled sanity check

A fresh unprofiled run shipped with the captures:

| Arm | September 17 unprofiled mean |
|---|---:|
| `tile` | 6.90786 ms |
| `tile-fused` | **3.09687 ms** |
| `rts` | 3.61031 ms |

This session does not replace the paired September 15 result, but it preserves the same ordering. `tile-fused` remains 55.17% lower time than `tile` and 14.22% lower time than RTS.

## Runtime counter result

These values come from the three throughput traces and represent the **whole captured region**.

| Exported signal | `tile` | `tile-fused` | `rts` | Interpretation |
|---|---:|---:|---:|---|
| DRAM activity (% peak sustained elapsed) | 71.0448 | **58.5548** | 69.4588 | fused whole-capture DRAM signal is 12.49 pp below tile |
| DRAM read activity | 35.5850 | **19.6442** | 34.7299 | strongest directional support for removing a read-heavy conversion stage |
| DRAM write activity | 35.4598 | 38.9106 | 34.7290 | writes do not fall; this is not a uniform memory reduction |
| L2 activity | 23.3848 | 25.6579 | 27.2161 | fused is not simply lower at every cache level |
| SM throughput | 3.11154 | 3.85622 | 3.33324 | no evidence that lower SM utilization is the mechanism |
| active compute warps (% peak elapsed) | 28.7308 | 26.2211 | 58.3605 | fused does not show an occupancy-style improvement |
| allocated compute registers (% peak elapsed) | 17.7687 | 25.1431 | 38.4488 | register pressure signal rises in fused rather than falling |
| L1TEX sector hit rate | 54.8462 | 62.3106 | 65.8144 | cache behavior shifts along with the operation structure |
| pixel-shader active warps (% peak elapsed) | 0.00783379 | 0.179839 | 0.00241941 | nonzero graphics signal indicates device-wide background activity |

The critical result is not “DRAM fell by exactly 17.58% for the scan.” The correct statement is narrower: **the fused whole-capture region has materially lower DRAM activity, driven by a much lower read-side signal, and the old AddInput stage is absent.** That is directionally consistent with the preregistered pass-elimination hypothesis.

## Shader-profiler result

The real-time shader profiler makes the structural difference visible.

`tile` contains:

- `AddInput`: **13.1298%** active-warp signal;
- `SinglePassScanWaveTiled`: 12.8062%;
- `InitOne`: 3.70407%.

`tile-fused` contains:

- **no `AddInput` node**;
- `SinglePassScanWaveTiled`: 22.1342%;
- `InitOne`: 6.31978%.

Because these are fractions of different whole-capture executions, the scan-node percentages are not absolute shader times. They establish stage presence/absence and provide a workload-composition signal.

Selected PCSampler stall signals:

| Stall signal (% peak elapsed) | `tile` | `tile-fused` | `rts` |
|---|---:|---:|---:|
| long scoreboard / L1TEX | 16.2450 | **6.04934** | 27.2416 |
| barrier | 8.93252 | 15.4343 | 10.0307 |
| LG throttle | 2.21680 | 3.78106 | 8.50185 |
| short scoreboard | 0.539505 | 0.947765 | 8.60934 |
| membar | 0.396108 | 0.672593 | 0 |
| wait | 0.804533 | 0.858793 | 0.785445 |
| MIO throttle | 0.0111311 | 0.0357139 | 11.7194 |

The stall mix changes rather than uniformly improving. `tile-fused` shows substantially less long-scoreboard signal, but more barrier signal. This is compatible with a shorter operation whose internal bottleneck mix shifts after the redundant stage disappears. It is not a direct accounting of the 54.45% delta.

## Prediction check

The preregistered predictions can now be scored without retrofitting the story.

1. **Operation structure — supported.** The shader profiler shows `AddInput` in `tile` and no `AddInput` node in `tile-fused`.
2. **Lower memory-system work — directionally supported.** Whole-capture DRAM activity is 71.0448% for `tile` and 58.5548% for `tile-fused`; the read side drops from 35.5850% to 19.6442%. Exact scan-only DRAM bytes remain unmeasured.
3. **Occupancy need not improve — supported.** Active-compute-warps signal falls slightly and allocated-register signal rises in fused, while the independent timing remains much faster.
4. **RTS comparison — partially explained, not generalized.** RTS has higher whole-capture compute-active-warps and register-allocation signals, plus a different stall mix. The timing claim remains specific to this inclusive uint32 workload and size.
5. **Failure condition — not triggered.** The counter evidence does not contradict the pass-elimination/memory-work hypothesis, but capture contamination prevents upgrading it to exact device-level byte attribution.

## Diagnosis

The best-supported explanation is now:

**The 54.45% complete-operation speedup is primarily a pass-elimination win.** The fused implementation removes a separate full-array `AddInput` shader stage. The later Nsight capture independently shows that stage disappearing and shows materially lower whole-capture DRAM activity, especially reads. At the same time, occupancy/register signals do not improve, so the result should not be framed as an occupancy optimization.

The remaining uncertainty is quantitative hardware attribution. Because the capture spans multiple submits and contains background graphics activity, this evidence cannot say how many DRAM bytes the scan itself saved or assign the entire timing delta to a specific counter.

Interviewer-safe statement:

> I first established a 54.45% complete-operation win with paired GPU timestamps and full correctness gates. The source change removed a separate 3 GiB logical full-array conversion stage. In a later RTX 4090 Nsight Graphics capture, the AddInput shader node disappeared and whole-capture DRAM activity, especially reads, fell materially, while occupancy/register signals did not improve. That supports pass elimination and reduced memory-system work as the mechanism, but I keep the hardware claim scoped because the trace covered multiple submits and included background GPU activity.

## Remaining high-value follow-up

Only one profiler task remains worth doing: add a stable user marker or frame boundary around one warmed scan operation and recapture the three arms in a quiet session. That would allow range-isolated metrics and a valid multi-pass setup where supported.

Until that is done:

- the frozen September 15 paired timestamps remain the performance truth;
- the September 17 counters are valid directional runtime evidence;
- no measured DRAM-byte reduction or isolated achieved-occupancy number should be claimed.

## Reproduction anchor

The frozen timing/correctness evidence, machine identity and exact reproduction wrapper are in [RTX4090_INCLUSIVE_SCAN_2026-09-15.md](RTX4090_INCLUSIVE_SCAN_2026-09-15.md). The capture evidence above used the same workload contract and source commit recorded in the evidence directory.
