# RTX 4090 inclusive scan: hardware-profile diagnosis

Status: **timing-confirmed; runtime hardware counters pending local capture**.

This case turns the September 15 inclusive-scan result into a profiler-driven diagnosis rather than inventing counter evidence after the fact. The measured performance result is already frozen: at `2^28` uint32 elements, the previous exclusive-scan-plus-conversion path took **6.376 ms**, the native-inclusive fused path took **2.904 ms**, and pinned GPUPrefixSums RTS took **3.584 ms**. The fused path therefore reduced complete GPU operation time by **54.45%** versus the old path and used **18.97% less time** than RTS on this exact RTX 4090 workload.

The existing source accounting also gives a concrete hypothesis to test. The old path performs a separate full-array conversion after scan. At the main problem size that pass logically reads input and output and rewrites output, or **3 GiB of full-array traffic**. The fused path emits inclusive values while the vector is already loaded and removes that full-array stage. Timing proves the operation became faster; a hardware capture is needed to prove *why* at the device level.

## Performance question

**Did the 6.376 -> 2.904 ms improvement primarily come from eliminating full-array memory traffic and an extra operation stage, or did a change in shader execution efficiency materially contribute?**

This is deliberately falsifiable. If the profiler does not show a substantial reduction in memory-system work and operation-level dispatch/synchronization cost, the diagnosis must be revised.

## Arms

Use the same frozen native workload and inputs as the confirmed result:

| Arm | Meaning | Confirmed complete GPU operation |
|---|---|---:|
| `tile` | original exclusive scan + AddInput conversion | 6.376 ms |
| `tile-fused` | native inclusive output in the local GPUPrefixSums-derived adaptation | **2.904 ms** |
| `rts` | pinned GPUPrefixSums ReduceThenScan | 3.584 ms |

Do not tune parameters during capture. Keep `2^28` elements, the existing shader binaries/settings, and the same GPU/driver identity when possible.

## Capture tool

For the RTX 4090, use **NVIDIA Nsight Graphics GPU Trace** as the primary runtime-counter tool. GPU Trace supports D3D12 on NVIDIA Turing and newer GPUs and exposes timeline throughput/occupancy metrics, multi-pass metrics, stall information, memory-system activity, and shader profiling. PIX timing capture is a useful second view for D3D12 command/queue timing and background GPU activity, but it is not a substitute for the NVIDIA hardware-counter diagnosis.

References:

- NVIDIA Nsight Graphics GPU Trace overview: <https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-overview.html>
- NVIDIA GPU Trace UI / metrics reference: <https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-ui.html>
- NVIDIA Shader Profiler: <https://docs.nvidia.com/nsight-graphics/UserGuide/shader-profiler.html>
- Microsoft D3D12 timing guidance / PIX starting point: <https://learn.microsoft.com/en-us/windows/win32/direct3d12/timing>

## Capture controls

The counter session is diagnostic evidence, not a replacement for the 18-process confirmation. Prefer repeatable, quiet captures over a large sample count.

1. Reboot or otherwise return the machine to a quiet state; close browsers, overlays, launchers, video playback and other GPU-active applications.
2. Record GPU model, driver, build SHA, shader/source identity and Nsight Graphics version.
3. Use the same problem size and input contract as the September 15 result.
4. Warm each arm before the measured capture. Keep warmup policy identical across arms.
5. In Nsight Graphics, use GPU Trace with throughput metrics first. Then collect multi-pass metrics for the same stable region. Lock clocks consistently if the tool/hardware permits it and record whether that option succeeded.
6. Capture each arm separately so unrelated work does not overlap the operation of interest.
7. Check the trace for other-process GPU execution. If another process overlaps the measured range, retain the trace as contaminated evidence but do not use it for the primary comparison.
8. Keep the original timestamp result as the performance truth. Profiler runs can perturb timing and should be used to explain the already-confirmed delta, not to replace it.

Nsight Graphics recommends consistent user-marker order for multi-pass metrics and warns that other processes can invalidate metric regions. If the current native harness does not expose a clean frame boundary, delimit or isolate a single warmed operation before treating a multi-pass trace as comparable evidence.

## Counters and questions

Record the closest available metrics in the installed Nsight Graphics version. Exact counter names can vary; preserve exported names in the evidence rather than normalizing them after capture.

| Area | Record | Question |
|---|---|---|
| GPU time | operation/range duration; dispatch timing | Does the trace reproduce the ordering `tile-fused < rts < tile`? |
| DRAM / memory | DRAM throughput/bytes or memory-unit activity | Does removing the conversion pass materially reduce off-chip traffic/work? |
| L2 | L2 throughput, hit behavior, read/write activity | Was the removed pass partly cache-served, or did it propagate substantial traffic below L2? |
| SM | SM throughput / active cycles | Is the fused path less shader-work-heavy, or simply shorter because a pass disappeared? |
| Occupancy | achieved/active warp occupancy and limiting factors | Did occupancy improve, stay similar, or regress while total time still fell? |
| Registers | shader register pressure / occupancy limit if exposed | Did fused inclusive-prefix instructions increase register pressure? |
| Shared memory | shared-memory/L1TEX activity and occupancy limit if exposed | Is the local scan structure still limited by shared-memory behavior? |
| Warp stalls | dominant stall categories from multi-pass/shader profiling | Are memory/dependency/barrier stalls reduced, unchanged, or shifted? |
| Synchronization | command-list gaps, barriers, queue idle regions | How much operation-level overhead disappeared with the removed conversion stage? |
| Other-process load | overlapping contexts/processes | Is the counter range clean enough to interpret? |

## Preregistered predictions

These predictions are written before the hardware-counter capture so the report cannot be retrofitted around whatever the profiler happens to show.

1. **Operation structure:** `tile-fused` should contain less complete-operation work than `tile` because the separate conversion stage is absent. The trace should make that structural difference visible in dispatch/copy/barrier sequencing.
2. **Memory-system work:** `tile-fused` should show materially lower full-operation memory activity than `tile`. The source-level upper-level accounting removes 3 GiB of logical full-array reads/writes; the hardware counter delta does not need to equal 3 GiB because caches, transaction granularity and counter definitions differ.
3. **Occupancy is not required to improve:** the performance win is expected to survive even if fused local-prefix instructions slightly increase register pressure or leave occupancy unchanged. A pass-elimination win should not be narrated as an occupancy optimization without evidence.
4. **RTS comparison:** if `tile-fused` remains faster than RTS, inspect whether the difference is explained by operation structure/memory traffic rather than claiming a generally superior scan algorithm. The result remains specific to inclusive uint32 scan at this size and configuration.
5. **Failure condition:** if the fused arm does not show lower memory-system/operation-stage cost, or if profiler evidence points to another dominant mechanism, replace the memory-traffic hypothesis with the observed limiter. Do not preserve the original story for presentation value.

## Result table — fill only from exported capture evidence

Do **not** estimate these fields from timing or source code.

| Metric | `tile` | `tile-fused` | `rts` | Interpretation |
|---|---:|---:|---:|---|
| GPU range / operation time | pending | pending | pending | profiler run only; compare with frozen timestamps |
| DRAM activity / throughput | pending | pending | pending | |
| L2 activity / throughput | pending | pending | pending | |
| SM throughput | pending | pending | pending | |
| achieved occupancy | pending | pending | pending | |
| register pressure / limit | pending | pending | pending | |
| shared-memory / L1TEX signal | pending | pending | pending | |
| dominant warp stall(s) | pending | pending | pending | |
| dispatches / operation stages | pending | pending | pending | |
| overlapping external GPU work | pending | pending | pending | capture-quality gate |

## Current diagnosis

What is established now:

- complete GPU operation time fell from **6.376 to 2.904 ms**;
- the fused path used **18.97% less time** than pinned RTS in this exact workload;
- the source change removed a logically **3 GiB** full-array conversion stage at `2^28` elements;
- correctness and timing were independently gated in the existing September 15 evidence.

What is **not** established yet:

- measured DRAM-byte reduction;
- measured L2 behavior;
- achieved occupancy;
- register/shared-memory occupancy limits;
- warp-stall distribution;
- a hardware-counter attribution of the 54.45% improvement.

The interviewer-safe statement before capture is therefore:

> I first proved a 54.45% complete-operation improvement with paired GPU timestamps and full correctness gates. Source accounting suggested that eliminating a separate 3 GiB full-array conversion pass was the main mechanism. I treat that as a hypothesis, not a counter result; the next diagnostic step is an Nsight Graphics GPU Trace comparing memory-system activity, occupancy, stalls and operation structure across the old, fused and RTS arms.

After a clean capture, replace the pending table with exported values, add screenshots/trace hashes if redistribution is permitted, and rewrite this paragraph around the counters that actually explain the bottleneck.

## Reproduction anchor

The frozen timing/correctness evidence, machine identity and exact reproduction wrapper are in [RTX4090_INCLUSIVE_SCAN_2026-09-15.md](RTX4090_INCLUSIVE_SCAN_2026-09-15.md). Reuse that workload rather than creating a profiler-only synthetic input.
