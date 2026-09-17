# RTX 4090 Nsight Graphics capture evidence — 2026-09-17

This directory records the compact, reviewable evidence extracted from the local Nsight Graphics capture archive for the inclusive-scan case.

## Identity

- GPU: NVIDIA GeForce RTX 4090, Wave32.
- Driver: 591.86 (`32.0.15.9186`).
- Nsight Graphics CLI: `2026.3.1.0` build `38722833`.
- Source commit captured: `0b2e224928a344b8e5724aec1a50a8cedbc801f2`.
- Workload: `268,435,456` uint32 values (`2^28`), same all-one inclusive-scan contract as the frozen September 15 result.
- Native harness: one excluded warmup plus 100 timing iterations; full-size validator passed for all three unprofiled sanity-check arms.
- Original local archive SHA-256: `61a5113f093c3164d21187572bfab7bfa029fdc62c8cee1a44e56a2fc665a225`.

The original archive contains native `.ngfx-gputrace` reports, NVIDIA-exported tables, lossless CSV conversions, CLI logs, command arguments, GPU process snapshots, GPU samples, source/runtime identity and a per-file SHA-256 manifest. The binary reports are intentionally not committed here; this directory preserves the compact exported values needed to review the public diagnosis.

## Capture scope

The throughput/shader traces start after 20 submits and are limited to 6 submits. The inherited native loop submits input generation, the scan work, and query resolve separately. There were no user-marker rows in `GPUTRACE_REGIMES`.

Therefore the exported values below are **whole-capture aggregates**, not scan-only counters.

This matters:

- do not compare profiler-capture duration directly with the frozen `6.376 / 2.904 / 3.584 ms` operation timings;
- do not derive DRAM bytes from activity percentages;
- do not call the active-compute-warp signal an isolated shader achieved-occupancy measurement;
- do not normalize PCSampler stall percentages into a pie chart;
- do not attribute device-wide background activity to a specific process without timeline/process correlation.

Nsight also rejected true multi-pass collection for these submit-limited traces. Those three retained attempts are evidence of the limitation, not valid multi-pass results.

## Unprofiled sanity check

A fresh unprofiled run in the same delivery preserved the expected ordering:

| Arm | Mean complete GPU operation |
|---|---:|
| `tile` | 6.90786 ms |
| `tile-fused` | **3.09687 ms** |
| `rts` | 3.61031 ms |

`tile-fused` remained 55.17% lower time than `tile` and 14.22% lower time than RTS in this separate session. The absolute numbers are not substituted for the frozen September 15 paired result.

## Whole-capture throughput signals

| Exported signal | `tile` | `tile-fused` | `rts` |
|---|---:|---:|---:|
| DRAM activity (% peak sustained elapsed) | 71.0448 | **58.5548** | 69.4588 |
| DRAM read activity | 35.5850 | **19.6442** | 34.7299 |
| DRAM write activity | 35.4598 | 38.9106 | 34.7290 |
| L2 activity | 23.3848 | 25.6579 | 27.2161 |
| SM throughput | 3.11154 | 3.85622 | 3.33324 |
| Active compute warps (% peak elapsed) | 28.7308 | 26.2211 | 58.3605 |
| Allocated compute registers (% peak elapsed) | 17.7687 | 25.1431 | 38.4488 |
| L1TEX sector hit rate | 54.8462 | 62.3106 | 65.8144 |
| Pixel-shader active warps (% peak elapsed) | 0.00783379 | 0.179839 | 0.00241941 |

The strongest directional signal is the DRAM read side: the fused capture is 15.9408 percentage points lower than the old `tile` capture while the write side is not lower. That is **consistent with** removing a separate read-heavy full-array conversion stage. It is not an isolated scan byte count.

The active-compute-warp signal does not improve in the fused capture, and the register-allocation signal increases. The timing win therefore should not be narrated as an occupancy or register-pressure optimization.

The nonzero pixel-shader activity is a capture-quality warning because this harness creates compute work, not a graphics workload. It is consistent with other desktop GPU work being present during the device-wide trace.

## Shader-profiler structure and stall signals

The real-time shader profiler identifies the old conversion stage directly:

- `tile`: `AddInput` active-warp signal = **13.1298%** of peak elapsed.
- `tile-fused`: there is **no AddInput shader node**.
- `tile`: `SinglePassScanWaveTiled` = 12.8062%.
- `tile-fused`: `SinglePassScanWaveTiled` = 22.1342%.

The latter percentages are fractions of different whole-capture executions; they should not be interpreted as the fused scan kernel taking 1.73x as much absolute time.

Selected PCSampler stall signals:

| Stall signal (% peak elapsed) | `tile` | `tile-fused` | `rts` |
|---|---:|---:|---:|
| Long scoreboard / L1TEX | 16.2450 | **6.04934** | 27.2416 |
| Barrier | 8.93252 | 15.4343 | 10.0307 |
| LG throttle | 2.21680 | 3.78106 | 8.50185 |
| Short scoreboard | 0.539505 | 0.947765 | 8.60934 |
| Membar | 0.396108 | 0.672593 | 0 |
| Wait | 0.804533 | 0.858793 | 0.785445 |
| MIO throttle | 0.0111311 | 0.0357139 | 11.7194 |

The stall mix changes rather than uniformly improving. The fused capture shows much less long-scoreboard signal but more barrier signal. Because these are whole-capture samples with their original Nsight denominators, they are diagnostic clues, not a causal accounting of the 54.45% timing delta.

## Supported diagnosis

The captured evidence strengthens, but does not fully prove, the original mechanism:

1. The frozen timing result already proves the fused complete operation is much faster.
2. The shader profiler directly confirms that the old `AddInput` stage exists in `tile` and is absent from `tile-fused`.
3. Whole-capture DRAM activity, especially DRAM read activity, is materially lower in the fused arm.
4. Occupancy/register signals do not improve, so the result should not be sold as an occupancy win.
5. The capture contains background graphics activity and no marker-isolated scan range, so a device-level causal claim about exact DRAM reduction remains out of scope.

The interviewer-safe conclusion is therefore:

> The optimization removed a separate full-array AddInput stage. A later RTX 4090 Nsight capture independently showed that the AddInput shader disappeared and that whole-capture DRAM activity, especially reads, fell materially, while occupancy/register signals did not improve. That supports pass elimination and reduced memory-system work as the mechanism, but I do not claim scan-isolated DRAM bytes because the trace covered multiple submits and contained background GPU activity.

## What would close the remaining gap

For a strict hardware-counter attribution, instrument a warmed scan operation with a stable user marker/frame boundary and recapture the three arms in a quiet environment. Use a range that isolates the scan operation and, where supported, valid multi-pass metrics. Until then, the frozen timestamps remain the performance truth and this evidence remains directional runtime diagnosis.
