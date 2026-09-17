# Crowd/VFX stage breakdown: why CPU12 wins this caller

This note decomposes the September 16 complete-task result without changing its scope.
The measured contract is CPU-generated input -> renderer -> complete RGBA atlas -> CPU-visible
buffered file export. It is not a GPU-resident presentation workload.

## Headline

Wave-tiled costs 48.04 ms/request across the 12-request lifecycle versus 6.98 ms/request
for CPU12 (6.88x higher). The gap has two different causes:

1. **Short-lifetime initialization dominates the GPU lifecycle.** Wave-tiled first use is
   455.53 ms. Device creation averages 224.35 ms and preparation 184.42 ms: 408.77 ms,
   or about 89.7% of first use. Spread across only 12 requests, those two initialization
   components alone contribute about 34.06 ms/request. CPU12 first use is 49.78 ms.
2. **CPU12 still wins after reuse.** The reused-request mean is 7.56 ms for wave-tiled and
   3.04 ms for CPU12 (2.49x). This is therefore not only a cold-start/device-creation story.

## Steady request

The CPU implementation does not revisit all 8,388,608 seeds every frame. Its charged
preparation step reduces the immutable input to 16,237 statically eligible agents. The
moving visibility and direct integer splat then operate on that reduced set.

For CPU12, steady rendering averages 0.824 ms and buffered export averages 2.210 ms.
Those two values essentially account for the 3.04 ms reused request: roughly 27% renderer
and 73% export. Adding more GPU scan work cannot materially improve this CPU path.

Wave-tiled has a different shape:

| Stage | Mean | Interpretation |
| --- | ---: | --- |
| GPU render | 3.794 ms | visibility + scan/compact + tile binning + raster |
| GPU readback copy | 0.359 ms | device output copied to readback resource |
| CPU fence wait | 4.379 ms | host waits for submitted GPU work/readback to complete |
| Buffered export | 2.173 ms | nearly the same output cost as CPU12 |
| Reused request | 7.562 ms | enclosing wall-clock request |

The rows above are **not additive**. In particular, GPU execution/readback happen while the
CPU is inside the fence-wait interval. The fence number is evidence of synchronization on
this CPU-output contract, not a second independent 4.379 ms of GPU work.

Two conclusions are robust from the measured data:

- The GPU renderer itself is about 4.60x slower than CPU12's renderer for this particular
  prefiltered integer-splat workload (3.794 ms versus 0.824 ms).
- Export is approximately equal on both paths (~2.2 ms), so it does not explain the GPU/CPU
  difference. The differentiators are GPU setup/lifetime cost and the GPU execution +
  synchronization/readback path required to return a CPU-visible atlas.

## Why the primitive win did not transfer

The earlier inclusive-scan result measured a GPU primitive. This caller measures an
application contract. Here, scan is only one part of visibility, compaction, binning,
rasterization, transfer, synchronization and export. CPU12 also exploits application-level
structure unavailable to the primitive benchmark: it pays once to cache the 16,237 static
eligible agents and then performs a very small direct render.

Therefore the correct engineering conclusion is not "the GPU scan is bad". It is:

> Under a short 12-request, CPU-input/CPU-output contract with a highly reducible scene,
> the complete GPU path cannot amortize device/preparation cost and remains slower after
> reuse because the CPU renderer has a cheaper application-specific working set.

A GPU-resident consumer changes that contract. If the next stage consumes the compacted
visible set or rendered result on GPU, the readback/fence/export boundary can disappear and
must be measured as a separate workload rather than inferred from this result.

## Reproducing the stage view

For new or extracted complete-task runs:

```text
python tools/summarize_crowd_stages.py <run-or-extracted-evidence-directory>
```

The analyzer reports GPU render/readback/record/submit/fence/copy/export or CPU render/export
means from the measured request records. It deliberately keeps overlapping GPU and fence
intervals separate.
