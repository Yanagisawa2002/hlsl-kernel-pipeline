# GPU-driven Crowd/VFX application proof

## Status

The standalone implementation and two independent four-level GPU matrices are
complete. On an AMD Radeon AI PRO R9700, all 1,224 candidate executions across
the two runs matched the CPU oracle. The primary extreme-pressure result reduced
the complete application plan from 5.09944 ms to 2.74194 ms, or 1.8598×; the
replication measured 1.8689×. See the
[full results, raw samples, media, and limits](results/R9700_CROWD_VFX_2026-09-03.md).

The safe validation command does not create a D3D12 device:

    dotnet run --project src/HlslPerf.GpuDrivenDemo -c Release -- validate

GPU work requires the explicit command below:

    dotnet run --project src/HlslPerf.GpuDrivenDemo -c Release -- run --level all

## What the application actually does

Each plan generates twelve frames of a deterministic moving Crowd/VFX field. A
frame is not a decorative post-process added after a microbenchmark; the timed
plan itself performs the complete data path:

    agent seed buffer
          |
          v
    visibility / alive predicate
          |
          v
    compacted visible-seed list
          |
          v
    screen-tile histogram -> exclusive offsets -> per-tile lists
          |
          v
    deterministic tiled compute raster
          |
          v
    480 x 270 x 12 RGBA atlas

The moving camera selects a deterministic window of a larger virtual world.
Visible particles move in both axes, are assigned one of three emissive color
families, and are binned into 16-pixel screen tiles. The raster pass reads only
the current tile and its neighbors and adds the same integer glow function used
by the CPU oracle.

This is an application-level compute renderer over the public raw-buffer ABI.
It intentionally does not pretend to be an `ExecuteIndirect` graphics backend:
adding indirect draw/mesh dispatch is a separate backend extension and is not
required to measure the data-preparation pipeline honestly.

## The controlled A/B

The materialized baseline performs:

1. predicate evaluation into an `N`-element flag buffer;
2. hierarchical exclusive scan into an `N`-element prefix buffer;
3. visible-seed scatter;
4. globally contended screen-tile atomics;
5. tile prefix, tile scatter, and raster.

The optimized family performs:

1. fused predicate -> wave prefix -> decoupled look-back -> direct scatter;
2. replicated LDS tile histograms followed by a small global merge;
3. the same tile prefix, tile scatter, and raster.

The important claim under test is reduced end-to-end memory traffic and atomic
contention. The optimized path avoids writing and rereading two full-size arrays
for every frame; it does not change scene quality or omit downstream work.

Candidate axes cover group size, elements per thread, scalar/`uint4` input,
Wave32/64, fused item scale, persistent groups, and LDS histogram replicas.
Manifest conditions prevent fused-only axes from contaminating the baseline,
and a vector constraint removes invalid EPT products. Each measurement pressure
level expands to 153 bounded candidates.

## Evidence contract

Every candidate is timed as its complete multi-pass, twelve-frame application
plan. Upload, readback, and media composition are outside GPU timestamps. After
timing, the backend overwrites the entire atlas with `0xA5`, re-executes the
already-used plan, and compares the readback SHA-256 with the independent CPU
oracle.

The runner stores one content-addressed raw atlas because every correctness-
passing candidate must produce exactly those same bytes. Reports still retain
per-candidate hashes, samples, stability, and selection decisions. This avoids
temporarily writing hundreds of duplicate multi-megabyte captures.

The visual compositor does not speed up or slow down either video arbitrarily.
It submits one application update per chosen deadline and replays the recorded
GPU sample sequences. The two panels select frames from the same byte-identical
actual GPU atlas. A truthful overlay shows completed frames, backlog, missed
deadlines, and outstanding GPU work.

If the requested 120 Hz budget is not crossed, the runner may choose a deadline
strictly between selected p95 and baseline median. Such media is labeled
`measured-fit deadline`; it is never labeled 120 Hz. If no guarded interval
exists, no budget-crossing GIF is emitted.

## Pressure matrix

| Level | World agents | Alive/LOD mask | Target visible/frame | Expected candidates |
|---|---:|---:|---:|---:|
| Low | 262,144 | 15 | about 4K | 153 |
| Medium | 1,048,576 | 63 | about 4K | 153 |
| High | 4,194,304 | 255 | about 4K | 153 |
| Extreme | 8,388,608 | 511 | about 4K | 153 |

All levels render 480 x 270 x 12 frames. Increasing the mask keeps viewport
density and raster quality comparable while total simulated-world processing
grows. It does not give the optimized candidate a different predicate: baseline
and optimized paths within a level receive the same agents, mask, camera, bins,
and pixel oracle. The pressure sweep therefore emphasizes the intended sparse-
selection regime without hiding downstream raster work.

The run writes a CSV and SVG pressure grid so the strongest *guarded* whole-plan
advantage is selected from measurements instead of preselected for marketing.
Candidate checkpoints can be resumed only with the original output directory,
exact source identities, and retained content-addressed capture.

## Validation and measured evidence

The CPU/DXC validation proves:

- all four manifests validate and expand to 153 candidates;
- the smoke baseline builds 30 application passes and the fused path builds 21;
- both paths share one deterministic CPU atlas identity;
- all entry points used by both plans compile under DXC strictness with warnings
  treated as errors;
- deadline replay is regression-tested and never mutates timing samples;
- the full solution and 39 tests build and pass without opening a GPU.

The two explicit R9700 GPU runs establish:

- 612/612 correct candidates per run and byte-identical atlas hashes between
  runs;
- guarded primary speedups of 1.0615×, 1.1421×, 1.4075×, and 1.8598× from low
  through extreme pressure;
- a replicated extreme result of 1.8689× and a same-fixed-candidate result of
  1.8480×;
- an actual-GPU-atlas GIF/MP4 using an honestly labeled 253.8 Hz measured-fit
  deadline because 120 Hz was not crossed.

v0.7 remains the place for multi-vendor runs, randomized
interleaving/confidence intervals, and RGP/PIX counters. An optional future
graphics adapter can consume the compacted and binned lists through
`ExecuteIndirect` or mesh dispatch without moving the core workload into Unity.
