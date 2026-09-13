# Radix repair handoff, 2026-09-08

Status: implemented, compilation and CPU model checks passed, **Unmeasured**. Baseline
`9bba9fefb0c579052d4dfee429531c4145e9e4be`; branch
`codex/repair-20260908-hlsl-sort`.

This task owns `kernels/radix-sort.hlsl`, `RadixSort*` workload code, new
radix-specific layout code, and isolated CPU tests. Integration retains ownership
of `UnifiedWorkloads`, the shared operation ABI, D3D12 execution, the AMD adapter,
external benchmark entry points, SDK packaging, README and CI. No edits to
`scan_u32.hlsli` are planned.

Opt-in: `HLSLPERF_RADIX_TILE=1`, radix 4 or 8 bits, group 128,
four records/thread, scalar records, wave32, addition scan backend 2.
The default is zero. Existing workload IDs and ABI remain intact. The candidate
uses wave histograms and stable local reordering, a radix-specific chunk prefix
layout, and direct SoA final scatter for ABI-v2 pairs. No default promotion.

External references resolved from their true upstreams:

- `b0nes164/GPUSorting`: `09a6081d964b682bbb58838b83ab75aebe7f4e05`.
- `GPUOpen-Effects/FidelityFX-ParallelSort`:
  `0c539948c8d196ae338d91efbc8ca495f1ea0d1d`.

The integration task owns their native benchmark adaptation. This task provides
a pinned source/semantic review and independent internal optimization.
No benchmark, GPU dispatch, Player, profiling, calibration or real timing is
authorized. Historical RGA files have not been edited. Initial Git status without
long-path support reported six paths as modified; `git -c core.longpaths=true
status` resolves them without changes.

## Host contract

`RadixTileLayout.Create(count, bitCount, radixBits, pairs)` validates the existing
managed buffer ABI without allocating records. `DescribePlan(splitPairs)` returns
the actual managed workload's complete buffer/pass schedule; it is not a second
hand-maintained schedule. `Defines` contains every fixed candidate define.
Use `splitPairs=true` for ABI-v2 pairs. ABI-v1 pairs keep packed output.

`CreateForNativeBuffers` additionally supports native allocation lengths beyond
signed `int`, while retaining uint byte addresses in HLSL and the bounded prefix
layout. It does **not** enlarge `KernelBufferSpec`, change the SDK buffer ABI, or
establish GPU memory availability. In particular the unaltered upstream workload
of `2^28` pairs requires a 2 GiB packed buffer. Its exported sort plan has
9,665,774,592 logical bytes in total before host overhead; the original native
SoA outputs can be bound as the final output buffers. Do not shrink that workload
or drop payloads to pass an allocation check. Report unsupported on a real host
if the original workload cannot be prepared.

The plan exporter has no D3D12 dependency and never executes its plan:

```powershell
dotnet run --project tools/RadixPlanExport/RadixPlanExport.csproj -c Release --no-launch-profile -- --count 268435456 --pairs --native-buffers --output artifacts/radix-tile-native-2p28.json
```

Other export options: `--bits 1..32`, `--radix-bits 4|8`, `--packed-output`,
`--count 0` for the sentinel guard. Defaults are 4097 records, full32 keys, 8-bit
digits. These are static-contract examples, not external benchmark inputs.

The shader remains `kernels/radix-sort.hlsl`. Root slots are unchanged:
`SRV(t0), SRV(t1), UAV(u0), UAV(u1), b0[8]`. Compile for SM6.6+, wave32,
addition/backend2, scalar loads, group128/items4; legacy ballot rank must be zero.

For positive `N`, let `R=2^radixBits`, `G=ceil(N/512)`, `C=ceil(G/512)`,
`H=R*G`, `S=R*C`. The three reused scratch buffers are:

| Buffer | Bytes | Contents |
| --- | ---: | --- |
| `radix-histogram` | `4*H` | Bin-major tile counts |
| `radix-tile-sums` | `4*S` | Bin-major 512-tile chunk totals |
| `radix-tile-prefix` | `4*(H+S+R)` | Tile prefixes, chunk prefixes, global bin bases |

Offsets within `radix-tile-prefix` are uint elements: tile prefixes start at 0,
chunk prefixes at `H`, bin bases at `H+S`. They are all fully overwritten every
digit, so repeat operations do not depend on prior scratch contents.

Every stage receives `[N,512,shift,mask,C,G,dispatchX,logicalGroups]`, where
`mask=(1<<min(radixBits,bitCount-shift))-1`. Dispatch uses
`X=min(65535,logicalGroups)`, `Y=ceil(logicalGroups/X)`, `Z=1`. Histogram, chunk
prefix and scatter entry points flatten with `groupId.y*dispatchX+groupId.x` and
ignore over-dispatched groups. The bin-prefix entry point uses exactly one group.

| Stage | Entry | Logical groups | t0 | t1 | u0 | u1 |
| --- | --- | ---: | --- | --- | --- | --- |
| Histogram | `BuildRadixHistogram` | `G` | Current records | null | Histogram | null |
| Chunk prefix | `PrefixRadixHistogramTiles` | `R*C` | Histogram | null | Prefix | Sums |
| Bin prefix | `PrefixRadixHistogramBins` | `1` | Sums | null | Prefix | null |
| Scatter | `ScatterRadixTile` | `G` | Current records | Prefix | Next records | null |

Keep the stage order and resource transitions/UAV ordering, especially the
disjoint writes into Prefix in stages two and three. Each digit reuses all
scratch and alternates `radix-a`, `radix-b`; allocate only the needed intermediate
buffers. On the last digit of ABI-v2 pairs, use `ScatterRadixTileToPairs` with
u0=sorted keys and u1=sorted payloads. There is no positive-count split dispatch.
Empty keys/pairs retain the existing physical zero sentinels, never a logical
dummy record.

For GPUSorting's native SoA producer, `PackRadixPairs` reads t0=original native
keys, t1=original native payloads, writes u0=packed input. Use `G` groups and
`[N,512,0,0,0,0,dispatchX,G]`; for empty input use one group and physical sentinels.
It moves the actual payload, not an index or generated substitute. The final
scatter writes back to the native SoA output bindings. Keep this conversion in
the complete adapter operation. Existing managed inputs are already AoS.

## Algorithm and theoretical costs

The candidate counts matching lane ballots in each 32-record segment, prefixes
16 segment counts per bin, and reorders the tile stably in LDS before global
scatter. Original order is `(tile, item*128 + virtualWave*32 + waveLane)`.
Virtual wave IDs are allocated locally because wave lanes need not equal
`SV_GroupIndex`; this only assigns input ownership. Original input position
decides rank. Separate validity masks exclude tail lanes even for digit 255.
The final partial digit selects only its requested bits while carrying all key
and payload bits unchanged. Each scatter loads its key/payload once; global
offsets are cached once per occupied tile/bin.

The former general wide scatter examines all earlier records per record,
quadratic in tile size. This candidate does ballot work proportional to digit
width per record and a fixed `R*16` local segment-prefix walk. It does not use a
device-wide lookback, spinning inter-group dependencies, or a reset dispatch.
Full32 uses 16 dispatches at radix8 or 32 at radix4, for both keys and AoS-input
v2 pairs. A native SoA-input adapter adds its packing pass. These are source
counts, **not measured speedups**.

Bin-major intermediate storage makes neighboring tile counts contiguous for
each chunk scan. It is reused instead of adding per-level generic scan buffers.
The single bin-prefix group serially visits `C` summaries per bin: 4 for 1 Mi,
16 for 4 Mi, 1024 for the native `2^28` example. That centralized stage, histogram
atomics under skew, ballot/register pressure, LDS use, barriers, and extra native
AoS conversion may still dominate. No candidate is promoted or added to an
automatic tuning/default implementation list.

## Pinned external review

[radix-upstream-review-lock.json](radix-upstream-review-lock.json) records immutable
URLs, lengths, Git blob SHA-1 and SHA-256 for 14 reviewed files. Each fetched blob
matched the pinned upstream tree. This is a review-source lock; the integration
task owns the full native source/vendor lock and benchmark adapter.

GPUSorting's [pinned D3D12 shaders](https://github.com/b0nes164/GPUSorting/tree/09a6081d964b682bbb58838b83ab75aebe7f4e05/GPUSortingD3D12/Shaders)
include uint32 keys/payloads and full32 LSD sorting. The reviewed code uses wave
multisplit ranks, ordered wave histograms and key/payload scatter. Our candidate
belongs to that public algorithm family and to the existing internal ballot
lineage; it is independently implemented, not a vendored GPUSorting execution
or a claim to invent radix/multisplit. No upstream code is patched here.

The [native input and validation shader](https://github.com/b0nes164/GPUSorting/blob/09a6081d964b682bbb58838b83ab75aebe7f4e05/GPUSortingD3D12/Shaders/Utility.hlsl)
initializes payload equal to key and checks monotonicity. Preserve that native
benchmark behavior, but do not use it alone as stability, arbitrary-payload,
permutation or key/payload-association proof. The separate CPU oracle here
compares complete records with arbitrary payloads and original-position ties;
equivalent GPU checks remain pending explicit future authorization.

The [original AMD header](https://github.com/GPUOpen-Effects/FidelityFX-ParallelSort/blob/0c539948c8d196ae338d91efbc8ca495f1ea0d1d/ffx-parallelsort/FFX_ParallelSort.h)
contains four-bit digits, 128-thread/four-record groups, ordered two-bit local
reorders, bin offsets, and optional payload movement under `kRS_ValueCopy`.
Source review supports stable ascending full32 key/payload scheduling; this
round provides no execution evidence. This original repository is distinct
from the already-vendored FidelityFX SDK revision
`c6efa6bf7f2027b3ec94f28578bb5965eabb9e55`; their identities are not interchangeable.

The reviewed D3D12 GPUSorting files carry MIT SPDX notices. Its complete
[repository LICENSE](https://github.com/b0nes164/GPUSorting/blob/09a6081d964b682bbb58838b83ab75aebe7f4e05/LICENSE)
also retains FidelityFX and DirectStorage MIT, CUB BSD-3-Clause, and bb_segsort
LGPL-2.1 text; do not relabel the entire repository solely MIT or strip those
notices. The original AMD [LICENSE.txt](https://github.com/GPUOpen-Effects/FidelityFX-ParallelSort/blob/0c539948c8d196ae338d91efbc8ca495f1ea0d1d/LICENSE.txt)
is its retained MIT notice. No external licensing terms or project license are
changed by this patch.

## Allowed validation and remaining limits

- Release Workloads build: passed, zero warnings/errors.
- 49 selected CPU tests passed. Four property-test cases contain 2,400 bounded
  model/oracle combinations across 0..1025 records, 1/4/5/8/9/16/31/32 bits,
  random/duplicate/equal/descending/extreme keys, and keys/arbitrary payloads.
  Other tests check 511/512/513 summary boundaries, full write/permutation
  coverage, v1/v2 output hashes, invalid configuration preflight, 2D dispatch,
  and native/managed schedule agreement without allocating large inputs.
  The [CPU receipt](../evidence/radix-tile-functional-2026-09-08.json) records the
  exact filter, counts and source Git blob identities, without timing results.
- 74 DXC compilations: original binary, original wide, legacy ballot, new tiled,
  keys/pairs, empty and conversion entry points. `cs_6_6`, HLSL2018, O3,
  strictness and warnings-as-errors. Largest static LDS is 22,548 bytes, below
  32 KiB. No RGA, live counters or GPU were used. The retained
  [compile receipt](../evidence/radix-tile-compile-2026-09-08.json) binds shader,
  shared include, compiler and DXIL hashes.
- Native `2^28` pair plan exported and checked, without record allocation.
- Existing third-party lock verification passed for 50 vendored files; the 14
  new review-file hashes matched their immutable upstream blobs.
- No GPU correctness, memory-residency, forward-progress, timing, benchmark,
  calibration, profiling or default-selection result exists for this source.

Reproduce the allowed checks only:

```powershell
dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~HlslPerf.Core.Tests.RadixTileTests|FullyQualifiedName~HlslPerf.Core.Tests.RadixSortTests|FullyQualifiedName~BallotCandidateRetainsFullOperationAndHasDistinctShaderIdentity'
python tools/compile_radix_tile.py --dxc '<DXC executable>' --output artifacts/radix-tile-compile
```

Do not invoke RadixSmoke, FocusedCostRunner, UnifiedBench, native TestSort,
BatchTiming, or mixed validation scripts this round. The integration task owns
the future execution guard and native benchmark entry, including preserving the
upstream `BatchTiming(1<<28,100,10,ENTROPY_PRESET_1)` workload. It remains unrun.
