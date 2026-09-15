# Crowd/VFX whole-task validation

## Source and task choice (before GPU experiments)

Source main: `397f0054fa92990e39f4f217f3335ea606d0222f` (2026-09-15).
This is the repository's **existing controlled compute-rendering workload**,
not a production deployment or an externally sourced application.

The caller is `src/HlslPerf.GpuDrivenDemo/Program.cs`: `RunGpuEvidence` calls
`RunLevel`, which constructs `CrowdVfxWorkload`, captures the actual RGBA atlas
(lines 260-300 in the starting revision), and supplies that atlas to
`CrowdVfxComposer.WriteBudgetCrossing` (lines 212-217). The scene and renderer
predate this experiment. `CrowdVfxWorkload.cs:137-228` schedules visibility,
stable compaction, tile histogram, exclusive tile offsets, tile scatter, and
the pixel renderer. `gpu-driven-demo/crowd-vfx.hlsl:169-193` uses each exclusive
prefix as the stable visible-list offset; its tile lists are consumed by
`RasterizeCrowdVfx`, which computes the original integer glow and background.

The input is the existing deterministic CPU-generated agent seed array
(`CrowdVfxWorkload.GenerateAgents`). GPU visibility evolves with frame/camera
position. Input seeds and allocated scratch can be reused for consecutive
animation intervals. The functional output is twelve 480 x 270 RGBA frames in
one atlas, exported for the existing media consumer. The experiment includes
that export, but does not claim graphics presentation, Unity integration, or
video encoding performance. Raw file writes are buffered writes plus close,
not durable-storage flushes.

Other inspected candidates were rejected: `ScanParticleWorkload` repeats the
same scan before visualization; `PrimitiveApp` only builds/checks primitive
plans; pinned GPUPrefixSums' native timing input has no application consumer.
Neither extra scan repetitions nor a checksum constitute the selected task.

## Audit finding and necessary repair

The original controlled renderer computes the entire CPU pixel oracle during
plan construction. It also uses the oracle's visible counts to size buffers
and choose downstream dispatches (`CrowdVfxWorkload.cs:86,193-198`). That is
useful for a correctness fixture, but is not a caller-independent runtime path.
The new application adapter must build without that oracle. It uses bounded
N-element visible/tile-list capacities, GPU-produced count headers, and a
bounded grid-stride consumer. The same repair applies to every arm. CPU oracle
work is retained separately for validation; it cannot decide allocation,
dispatch size, flags, offsets, or GPU output.

## Fixed comparison before discovery

- `hierarchical`: the original materialized flags + hierarchical Blelloch
  exclusive scan + scatter algorithm, group256 / four items per thread.
- `fused`: the existing fused visibility/scan/scatter application algorithm,
  group256 / sixteen items / wave32 / 256 persistent groups, with its existing
  two-replica tile histogram. This retains a strong existing application path.
- `wave-tiled`: materialized flags + explicit native **exclusive** wave-tiled
  scan + scatter, using the established 4096-element partition and four polls.
- `rts`: materialized flags + pinned GPUPrefixSums ReduceThenScan **exclusive**
  + scatter. Upstream shader files remain unchanged. GPU production writes zero
  vector padding; the consumer reads only N real flags/offsets, so no redundant
  host upload or array trimming is required.

All arms produce the same pixels and stable visible sequence. The common
materialized consumers are identical. Fused histogram differences are reported
in separate stage diagnostics and cannot be attributed to scan alone. No
inclusive/exclusive conversion is inserted: the previous inclusive 19% result
does not apply directly to this task's exclusive contract.

## Measurement and acceptance boundary

First use begins before agent generation, plan construction, device/PSO and
buffer preparation. It ends after complete GPU rendering, full atlas transfer,
CPU copy, and the raw RGBA file close. Steady requests reuse device, compiled
pipelines, seeds, scratch and readback allocation while advancing animation
frame constants. Each request is submitted and completed individually. Report
GPU rendering and GPU transfer intervals separately from CPU record, submission,
fence wait, copy and export. Also retain first-use latency and amortized latency
at the declared request count. No stage p95 values are added together.

Discovery and pass-level diagnostic timings are separate from confirmation.
The final registration records exact cases, candidates, source/binary/runtime
identities, full expected outputs, order, independent-process replication,
resource budget, main metric and failure conditions before confirmation begins.
Small, medium, large and dense/nonaligned adverse scenes are required. Any
correctness failure, unbounded allocation, source drift, missing process, device
loss, load-gate failure or inconclusive gain is retained and limits the claim.

Hardware work requires the campaign queue, `Local\CodexR9700VNextUnityGpu`,
independent load/disk checks, and no unrelated heavy processes. No settings,
global caches, or other tasks' processes are changed.
