# Complete Crowd/VFX comparison: no demonstrated GPU benefit for this caller

**81 fresh processes and 972 complete atlas checks passed.** The fixed wave-tiled
candidate did not demonstrate lower complete-task cost than external RTS.
Every tested conventional CPU worker configuration was substantially faster.
Further scan tuning is not justified for this particular CPU-input/export caller.

## Task and results

The task uses the repository's existing controlled offscreen Crowd/VFX renderer.
Each fresh process generates 8,388,608 agent seeds and executes 12 real requests.
Each request renders a 480 × 270 × 12-frame RGBA atlas. GPU paths consume their
own visibility/scan/compaction results in tile binning and rasterization, read
back the complete atlas, and export it to an ordinary buffered file. The CPU
renderer produces the same bytes by direct integer splatting, with immutable
eligibility/origin/color/background caches shared across workers.

The primary metric is **(first-use cost + next 11 complete requests + cleanup)/12**.
It includes input generation, renderer/device and shader preparation, initial
upload, all requests (including three labeled warmups), readback, file exports
and cleanup. Process startup outside the caller, reference validation and its
gaps, durable disk flush and display/presentation are excluded. This is a finite
caller-lifecycle measurement, not a frame-rate measurement.

| Implementation | Lifecycle cost per request | First use | Reused request mean |
| --- | ---: | ---: | ---: |
| Hierarchical GPU | 47.06 ms | 449.95 ms | 8.03 ms |
| Fused GPU | 45.12 ms | 448.85 ms | 6.08 ms |
| **Wave-tiled GPU** | **48.04 ms** | **455.53 ms** | **7.56 ms** |
| External GPUPrefixSums RTS | 48.85 ms | 485.28 ms | 6.90 ms |
| CPU, 1 worker | 11.87 ms | 54.48 ms | 8.11 ms |
| CPU, 2 workers | 8.95 ms | 52.27 ms | 4.93 ms |
| CPU, 4 workers | 7.71 ms | 50.94 ms | 3.75 ms |
| CPU, 8 workers | 7.28 ms | 51.17 ms | 3.27 ms |
| **CPU, 12 workers** | **6.98 ms** | **49.78 ms** | **3.04 ms** |

Nine independent processes per configuration ran in randomized cyclic orders;
every configuration occupied each order position once. The reused-request
column averages the last eight actual requests; those request samples are not
treated as independent processes for uncertainty calculations.

Wave-tiled versus RTS has a mean speedup of **1.0168x**, with an adjusted paired
bootstrap interval of **[0.9464, 1.0944]**. This does not establish a win. Its
complete cost is **6.88 times** that of the 12-worker CPU implementation; even
the single-worker CPU is much faster on this task. Fused is the best observed
GPU mean, but does not overturn the CPU conclusion.

## What explains the result

For wave-tiled, device creation averages **224.35 ms** and preparation
**184.42 ms**, substantial costs for a 12-request lifetime. Reuse alone does
not solve the gap: wave-tiled's reused request is 7.56 ms versus CPU's 3.04 ms.

The CPU renderer caches **16,237** statically eligible agents from the 8.39M
inputs, then applies the moving visibility and drawing logic to this reduced
set. Its preparation is charged to first use. In steady requests CPU rendering
averages **0.824 ms**, while file export averages **2.210 ms**. Wave-tiled GPU
rendering averages **3.794 ms**, readback **0.359 ms**, CPU fence wait **4.379 ms**,
and file export **2.173 ms**. GPU work and the CPU fence wait overlap; these
stage values must not be added together as independent costs.

These observations identify costly preparation and a workload that admits a
cheap CPU implementation. They do not isolate the effect of a new GPU caching
optimization: no such optimization or additional task was introduced here.

## Measurement conditions and limits

Windows/D3D12, RTX 4090, the existing .NET 10.0.10 build at
`3b16a2de608bf938ff7c5a9b44baee36fafae910`. The rented Linux RTX 5090 was not used.
The run occurred September 16, 00:43–00:45 Singapore time (September 15 UTC).
It uses the already validated `confirmation-large` input, seed 69501203 and
visibility mask 511; no case or candidate was selected after inspecting results.

This is **observed desktop background load**, not isolated GPU hardware.
GPU snapshots before/after processes ranged from **0% to 34%**; 35/81 processes
passed both strict quiet-load brackets. All 81 are retained, with no sample
replacement or background-cost subtraction. The brackets do not continuously
measure competing work during requests. RTS drift was +2.24%; CPU12 drift was
−1.72%, within the prospectively declared 10% reference drift limit.

Eight fixed candidate comparisons use 20,000 paired process bootstrap samples
and Bonferroni-adjusted two-sided intervals (99.375% per comparison, nominal
95% family coverage under the resampling assumptions). Results and intervals
are conditional on this input/session; they do not establish idle-GPU,
cross-machine, Unity or production-application performance. The earlier
quiet-only v2 protocol remains uncompleted. This separate fixed-task cohort
does not retroactively satisfy that protocol or convert primitive-only wins
into application wins.

## Decision and reproducibility

Use the conventional CPU path for this measured contract. Pause additional
wave-tiled tuning for the caller. A future GPU-resident consumer or genuinely
different changing-data workload would need its own real requirements and full
comparison before further investment.

- [Protocol](../../tools/focused-hlsl-protocol.json) and [runner](../../tools/run_hlsl_shared_fixed.py).
- [Summary](../evidence/crowd-full-task-20260916/summary.json) and [raw records](../evidence/crowd-full-task-20260916/raw-records.zip).
- The runner takes `--plan`, `--protocol` and `--output`; first prepare the bound
  build/validation plan using [the existing reproduction workflow](../integration/CROWD_REPRODUCTION.md).
  Run under the foreground named mutex. No automation is needed.

The 8.26 MB archive contains all per-process numerical/timing/compilation
records and logs, the exact executed runner and mutex launcher, frozen order,
sealed build receipt, and the full 144-frame reference atlas with its hashes.
All 972 emitted atlases were also byte-checked against that reference and their
on-disk hashes verified. The 6.05 GB of duplicate emitted atlas files remain
locally at `.scratch/focused-shared-20260916-01`; those duplicate copies are not
included in the archive. Archive SHA-256:
`b4d1c6e88c5002b329dc7e079f7fa2b8e43f9388aafcc8590f2ca123d0a153f9`.

The analysis was independently recomputed from all saved process records.
Synthetic paired-ratio, background-label and duplicate-process checks passed.
Source, binary, runtime and prior correctness bindings were checked before and
after the cohort. The committed runner adds a configurable plan path; the exact
executed version is retained in the archive. No native implementation changed.
