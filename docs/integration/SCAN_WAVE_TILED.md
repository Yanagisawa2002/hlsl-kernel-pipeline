# Opt-in wave-tiled Scan and compaction

Performance: **Unmeasured / 未测量，待验证**. This change supplies real HLSL,
CPU correctness models, compiler checks and opt-in workload descriptors. No GPU
dispatch, benchmark, profiling, counters, autotuning or Unity execution was used.
The default manifests and external comparison arms are unchanged.

The starting commit is `9bba9fefb0c579052d4dfee429531c4145e9e4be`, also `origin/main`
after fetch on 2026-09-08. The local `opt-next-hlsl-dynamic`,
`opt-next-hlsl-integration`, `focused-costs-hlsl` and `causal-hlsl` branch tips are
ancestors of that commit. Their code was inspected and retained. In particular,
the scalar/uint4 IO variant already existed; it is not a contribution of this change.

## Algorithm and source attribution

The new implementation is `kernels/include/hlslperf/scan_wave_tiled_u32.hlsli`.
It is a local adaptation of Thomas Smith's MIT-licensed
[GPUPrefixSums ScanCommon](https://github.com/b0nes164/GPUPrefixSums/blob/98d93a4e9ed2f3c8353119515bf9be90a2e137ad/GPUPrefixSumsD3D12/Shaders/ScanCommon.hlsl)
and [decoupled fallback](https://github.com/b0nes164/GPUPrefixSums/blob/98d93a4e9ed2f3c8353119515bf9be90a2e137ad/GPUPrefixSumsD3D12/Shaders/ChainedScanDecoupledLookbackDecoupledFallback.hlsl).
The original files and complete licenses remain unchanged under
`third_party/gpu-prefix-sums`. The new header retains attribution and the MIT
notice reference; it does not claim the upstream algorithms as original work.

[GPUSorting SweepCommon](https://github.com/b0nes164/GPUSorting/blob/09a6081d964b682bbb58838b83ab75aebe7f4e05/GPUSortingD3D12/Shaders/SweepCommon.hlsl)
was also reviewed for per-bin publication, bounded polling, fallback and reset.
Its counted radix-bin payloads have different bounds from an arbitrary uint32
sum. This change ports no radix sorter. `scan-wave-tiled-sources.json` records
both commits and the SHA256/git-blob identities of the reviewed originals.
GPUSorting review snapshots are retained in this worktree's ignored
`artifacts/scan-source-review/GPUSorting`; the lock gives immutable download URLs
for future review. They are not a build dependency or a renamed benchmark.

Both projects are algorithm libraries with native benchmarks. Neither is being
presented as a general standard workload suite. These CPU test patterns are
correctness fixtures, not external benchmark workloads. External suite discovery,
native-host integration and all future run authorization belong to the integration
task. PRK and measurement frameworks are not introduced as standard workloads.

## Mapping and local prefix order

Let `G` be group threads, `W` fixed wave size, `I` items per thread, `K=I/4`,
`q` the wave's allocated segment index, `l=WaveGetLaneIndex()`, and `B=G*I`. For vector `k`,
the first scalar address in partition `p` is:

```
p*B + q*W*I + (k*W + l)*4
```

The four components are consecutive original-order elements. Within one wave,
each vector instruction covers consecutive vectors across its lanes. At `I=16`,
neighboring lanes now start 16 bytes apart; the old per-thread-contiguous layout
starts them 64 bytes apart even when `Load4`/`Store4` is selected. All lanes still
read and write the full original array, including scalar tails.

Wave leaders allocate unique `q` values through a shared atomic counter once per
persistent group, then broadcast them to their lanes. The kernel does not infer
wave/lane packing from `SV_GroupIndex / W`. The reduction word doubles as this
startup counter, separated from spine use by a group barrier. Permuting physical
waves or their group-index packing changes ownership, not output order.

For each vector, the kernel computes four local exclusive prefixes, then scans
the vector sums across the wave. The prior vector-tile total is added to all four
prefixes. The last lane's inclusive sum provides that tile's total with
`WaveReadLaneAt`, reusing the prefix operation instead of also issuing
`WaveActiveSum` for every vector tile. This follows the upstream scan's reuse of
the inclusive wave total. The first wave scans all wave totals in parallel,
replacing the old single-thread loop over the spine. The second group barrier
makes those offsets visible to every wave.

Thus logical order is partition, wave segment, vector tile, lane, component.
Scanning a thread's sum across all of its vector tiles first would produce the
wrong order with this layout. The model separately enumerates this mapping and
compares every output with a sequential oracle.

Compaction uses the same prefix machinery on `(value & PredicateMask) == 0` flags,
retains input/flags in registers, and scatters selected values to `1 + prefix`.
It writes the count at word zero and preserves selected-value order. No full
flag or prefix array is materialized. This mask predicate differs from consumers
that supply a separate nonzero-predicate array; the Unity wrapper exposes only Scan.

## Full-width state, synchronization and progress

The state allocation stays `8 + 12*P` bytes, where `P=ceil(N/B)`:

| Offset | Meaning |
| --- | --- |
| 0 | Reserved; new reset writes zero |
| 4 | Atomic next-partition ticket |
| 8 + 12*p | Full uint32 aggregate |
| 12 + 12*p | Full uint32 inclusive partition prefix |
| 16 + 12*p | Status: 0 empty, 1 aggregate ready, 2 prefix ready |

No payload bits carry flags. Every arithmetic addition is modulo 2^32, including
the wave operations, fallback and partition prefix. The upstream fallback's
single-word `payload << 2 | flags` cannot represent all uint32 sums; this local
path uses separate immutable aggregate/prefix words without narrowing input values.

Only the partition owner writes its payloads. It stores the aggregate, executes a
device-memory barrier, then atomically publishes status 1. It similarly publishes
the inclusive prefix with status 2. Readers atomically observe status and execute a
device-memory barrier before loading the corresponding payload. A reader that saw
status 1 may consume the aggregate even if the owner subsequently publishes status
2, because the aggregate remains unchanged. `Output1` is globally coherent;
[that qualifier makes UAV fences device-wide](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/sm5-object-rwbyteaddressbuffer).
[Interlocked operations do not imply a fence](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/interlockedcompareexchange),
so the explicit barriers are required. Group synchronization surrounds shared
spine/control reuse, including the final output phase of each persistent iteration.

The leader consumes ready aggregate states moving left, or terminates at a ready
prefix. After at most `HLSLPERF_WAVE_TILED_MAX_POLLS` misses at one predecessor,
the whole group recomputes that predecessor's reduction from immutable input,
adds it privately, and moves left. Reduction uses group-striped uint4 loads; for
compaction it reevaluates the same mask predicate. It does **not** claim a lock,
publish into another partition's state, or wait for a publisher. A concurrent owner
publication during fallback is harmless: that partition is consumed exactly once.

Each successful aggregate/fallback step decreases the predecessor index; a prefix
terminates the traversal. A group that can continue executing therefore does not
require suspended predecessor groups to resume. This is not a claim about universal
GPU scheduling, bounded wall time, or freedom from device timeouts. Private fallback
can perform substantial redundant work, unlike the upstream fallback's shared
publication optimization. That is the explicit tradeoff for preserving the existing
32-bit atomic ABI without packed-payload truncation or multiword writer ownership.

`ResetWaveTiledState` invalidates every active partition status on **every** operation
and clears both header words. Payloads need not be cleared. It uses one group with
a strided loop over all `P` statuses, independent of the data-worker count. This
avoids the old epoch wraparound problem, including capacity shrink/grow reuse.
Reset and data dispatch must be ordered by a host UAV barrier or equivalent resource
transition. All previous uses of a shared scratch buffer must have completed in
queue order before resetting it; simultaneous operations require separate scratch.
The existing legacy entry points and their epoch/spinning protocol are retained
for reproducibility and are not silently relabeled as this new implementation.

## Minimal adapter contract

`WaveTiledScanCandidates.Create(32)` returns explicit defines; it does not execute
or register a default. Wave64 is also compiler-checked. The host rejects unsupported
operators, vector widths, wave/group shapes, item counts and unbounded poll counts.
The representative candidate has `G=256`, `W=32`, `I=16`, `B=4096`, and four status
polls. Generic min/max/xor stay on the existing implementations.

| Pass | Entry / bindings | Constants at b0 | Dispatch |
| --- | --- | --- | --- |
| Reset | `ResetWaveTiledState`, u0=scratch | `[0,0,P,0,0,0,0,0]` | `(1,1,1)` |
| Scan | `SinglePassScanWaveTiled`, t0=input, u0=output, u1=scratch | `[N,B,P,0,0,0,0,0]` | `(min(max(P,1),256),1,1)` |
| Fused compaction | `FusedCompactWaveTiled`, t0=input, u0=count+values, u1=scratch | `[N,B,P,mask,0,0,0,0]` | Same |

Use the existing ABI v1 root signature in `kernels/scan.hlsl` or `compaction.hlsl`.
Compile SM6.6+ with fixed wave support verified by the host. Input, output and
scratch must not alias. Input remains immutable throughout the operation. Buffers
use raw uint32 words. Scan writes precisely `N` exclusive results; compaction writes
the count and exactly that many selected values. Capacity after that logical
compaction output is not defined. A general consumer should allocate at least
`4*(N+1)` output bytes for compaction; the existing CPU-oracle workload can allocate
the known selected count plus one. Raw byte-address arithmetic requires
`N <= 0x3fffffff`; ABI v1's signed-int byte sizes impose a tighter host limit.

The kernels support `N=0`, `P=0`: dispatch one data group, Scan leaves the minimum
4-byte output sentinel unchanged, compaction writes a zero count, and reset needs
8 scratch bytes. Existing `KernelExecutionPlan` ABI v1 requires positive item counts;
that shared ABI is not changed. Native/engine adapters must handle this empty case
directly. For the requested SUMMIT range `0..256*65535`, all byte addresses fit.

`kernels/consumer/ScanWaveTiled.compute` provides the same implementation with Unity
name-bound raw buffers (`Input0`, `Output0`, `Output1`) and uint parameters
`ElementCount`, `ElementsPerBlock`, `LogicalBlockCount`. It has no embedded native
root signature. Include the shared header at its retained relative path. Bind
scratch to `Output0` for reset, then output to `Output0` and scratch to `Output1`
for Scan. Allocate scratch using capacity but clear the current operation's `P`.
There is no resource allocation or readback in the shader; the integration task owns
the persistent host recording adapter. The wrapper declares Unity's wave32 keyword
and DXC requirement. **Only standalone DXC compilation has been checked; Unity import,
capability selection and command-buffer synchronization remain unverified.**

For the GPUPrefixSums native benchmark, preserve the original input/size semantics,
test/oracle and whole-operation boundary. Its native inclusive timing entry must
not be mislabeled as exclusive. An inclusive adapter must include the conversion
`exclusive[i] + input[i]` and all reset/conversion barriers within its operation
boundary, or provide a separately verified inclusive kernel. The integration task
owns that thin host adapter and its disabled future run entry. The unmodified upstream
RTS remains at its three-uint4-per-thread, 3072-element partition defaults; this local
candidate is labeled an adaptation with its own 4096-element partition, not an
upstream score. No new local performance matrix is created here.

## Theoretical costs, not performance results

For fixed wave size, normal local scan work is O(N), with O(I) lane-local prefix
storage and O(G/W) shared storage. Compaction additionally retains O(I) values/flags.
Each vector tile has one wave-prefix operation and one last-lane broadcast. The
spine has one wave prefix and total reduction, with two group barriers. This
describes source operations, not hardware instruction counts or latency.
Startup adds `G/W` shared atomic increments, one wave broadcast and two group
barriers per persistent group, not per partition. It adds no shared words.

Without fallback, Scan logically reads and writes `4N` bytes each. The new reset
writes `4P+8` bytes and the state allocation remains `12P+8`. A private fallback
adds a read of up to `4B` bytes per missing predecessor. In the adversarial case,
there can be `P*(P-1)/2` such reductions, or O(NP) extra work. The reset has an
O(P) write cost every operation; the old epoch reset normally had constant work.

For the 256-thread candidate, DXC reports 52 declared shared bytes at wave32 and
36 at wave64 (`4*(G/W+5)`). The old 256-uint `ThreadTotals` array is absent from
these entries' IR. These are DXIL declarations, not physical LDS allocation,
register usage, occupancy, memory transactions or measured throughput. More
contiguous issued addresses and less declared shared storage do not establish a
speedup. No default algorithm was promoted.

## Non-performance verification and handoff

Executed in this isolated worktree:

- Release build of `tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj`: zero warnings/errors.
- 29 selected .NET tests: new opt-in/reset/buffer/shape contracts plus existing Scan
  hierarchy/single-pass and compaction plan tests. No D3D12 executor is referenced.
- Eleven deterministic Python CPU model tests: every output compared with a separate
  sequential oracle; wave32/64, multiple vector tiles, scalar/vector/partition
  tails, zeros/high-bit/full-u32 sums, stable compaction, poisoned state, shrinking
  and growing repeated operations, suspended predecessors, publication races,
  permuted group-index/wave packing, and a 6145-element full-width regression.
- 29 DXC compile contracts: 20 accepted shader compilations and nine intentionally
  rejected unsupported configurations, including both Unity wrapper entries and
  legacy entries. Static IR checks cover thread/wave metadata, vector loads/stores,
  shared declarations, wave total broadcasts, atomics and memory barriers.
- Four legacy DXIL binaries are byte-identical to separately compiled baseline
  `9bba9fe` sources with opt-in disabled. The control receipt is retained under
  `artifacts/scan-legacy-source-control/`; no shader was dispatched.
- All 50 existing third-party locked files pass the offline source verifier. The
  five additional GPUSorting review files have fixed byte/blob hashes in the scoped lock.

DXC path used: `C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/dxc.exe`.
The compile-only receipt and complete DXIL/IR/diagnostics are under
`artifacts/scan-wave-tiled-check-02/`; the receipt records compiler/source/binary hashes
and explicitly reports that GPU dispatch and Unity import were not executed.
`scan-wave-tiled-validation.json` commits a compact receipt with exact compiler,
checked-out source and DXIL identities, the selected test filter and limitations.
No performance clock was read by the new model or compiler-check code.

Safe non-performance reproduction, from the repository root:

```powershell
python tests/test_wave_tiled_scan.py
dotnet build tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --nologo
dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WaveTiledScanTests|FullyQualifiedName~WorkloadPackTests.Scan|FullyQualifiedName~WorkloadPackTests.CompactionExposesUnfusedAndFusedEndToEndPlans'
python tools/verify_external_sources.py
python tools/check_wave_tiled_scan.py --dxc 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/dxc.exe' --output artifacts/scan-wave-tiled-new-check
```

The compiler check accepts only compiler/output paths and refuses an existing output
directory; it has no dispatch or performance mode. GPU correctness, real cross-group
memory behavior, native-host inclusive conversion, Unity import and all performance
claims are untested. Only a new explicit user authorization may unlock the integration
task's future GPU/native benchmark run. Historical results remain associated with
their frozen sources (`824cfaf...` for the unified experiment, `70a343d...` for causal
confirmation); they are not evidence for the new source identity.
