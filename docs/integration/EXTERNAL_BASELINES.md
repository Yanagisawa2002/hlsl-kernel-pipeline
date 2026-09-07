# Official external baseline integration

This independent worktree starts at `6d61189eff3e64369753ceac6c1c3a5dcf112ec3`.
Previous measurements and archives remain historical evidence. This experiment
adds official external implementations; it is not a parameter search or an
industry-certified benchmark.

## Source identity and rights

`third_party/upstream-lock.json` records full commits, immutable download URLs,
file sizes, Git blob SHA-1 and SHA-256 for every vendored file. Run
`python tools/verify_external_sources.py` before building and sampling.
No vendored file is edited in place. Integration code lives outside these trees.

* AMD FidelityFX SDK v1.1.4 Parallel Sort: official
  [GPUOpen-LibrariesAndSDKs/FidelityFX-SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tree/c6efa6bf7f2027b3ec94f28578bb5965eabb9e55),
  commit `c6efa6bf7f2027b3ec94f28578bb5965eabb9e55`. The GPU headers, D3D12
  shader entry points, host scheduling reference and MIT notices are retained.
  This is the SDK release linked by the [official Parallel Sort page](https://gpuopen.com/fidelityfx-parallel-sort/).
* [GPUPrefixSums](https://github.com/b0nes164/GPUPrefixSums/tree/98d93a4e9ed2f3c8353119515bf9be90a2e137ad),
  commit `98d93a4e9ed2f3c8353119515bf9be90a2e137ad`. Use its D3D12
  `ReduceThenScan` and `ChainedScanDecoupledLookbackDecoupledFallback` HLSL.
  The latter is the upstream fallback-enabled single-pass implementation.
  Original MIT headers and the complete repository LICENSE are retained,
  including its separate CUB BSD notice; CUDA/CUB code is not vendored.

These third-party notices cover their respective files. The existing project
license and repository visibility are not changed by this integration. Public
release decisions and push are owned by the coordinating task.

## Algorithm lineage and default configurations

The earlier internal kernels were authored in this repository, as recorded in
its pre-integration provenance and Git history. They implement established
families: hierarchical wave scans, persistent chained lookback, and stable LSD
radix sorting using internal scan scratch. They are not novel inventions of
those algorithm families, and are not copies or wrappers of the newly vendored
AMD/GPUPrefixSums sources. The new arms execute the actual upstream HLSL.

AMD retains four bits/pass, 128 threads/group, four keys/thread, maximum 800
groups and eight full-key iterations, with the upstream capability-selected
wave64 permutation when supported. Its five dispatch stages per digit are
sum, reduce, scan, scan-add and scatter. Payload moves with keys.

Both GPUPrefixSums arms retain 256 threads, three uint4 values/thread and 3072
uint elements/partition. No wave-size attribute or tuning override is added.
ReduceThenScan uses the native exclusive propagation entry point. Fallback
uses its native exclusive entry point and its required InitCSDLDF dispatch
before every operation, with the upstream fixed spin limit of four.

Compiler settings are explicit per shader. Existing internal arms retain HLSL
2018, SM 6.6, O3 and strictness, matching the previous host path. GPUPrefixSums
uses HLSL 2021, SM 6.7 and O3 without an added strictness flag: its pinned host
supplies no language override to DXC 1.8 (whose default is 2021) and caps its
shader-model capability query at 6.7. AMD uses the pinned CMake wave64/full
precision permutation (`FFX_HALF=0`, `FFX_HLSL_SM=66`, SM 6.6), the two upstream
warning suppressions, and the active DXC default language (2021). It is compiled
with the shared current DXC, not claimed to be a bit-identical SDK prebuilt blob.
Compiler reference files and GPUPrefixSums packages.config are in the source lock.
Each process records actual compiler DLL file versions/hashes, per-shader options,
source/dependency hashes and DXIL hashes. No shader cache crosses processes.

Early development diagnostics 01 and 03 used the incorrect Vortice default
language/target/strictness for GPUPrefixSums. They encountered device removal;
dispatch isolation completed Reduce and Scan before failing at PropagateExclusive.
Diagnostic 02 could not load the optional D3D12 debug SDK. Diagnostic 04 restored
the upstream settings and passed all dispatches and both full-output poison checks.
These three setting changes were made together: the evidence does not isolate
any one setting as the sole cause. Original failures remain evidence and are not
formal samples. Fence handling now rejects the UINT64_MAX removal sentinel,
checks the device reason before submission and after completion, and has a
30-second wait limit. Native exceptions stop the process and the matrix epoch.

The fallback state stores flags in the low two bits of a 32-bit reduction.
Cross-partition large sums and uint32 wraparound must therefore be tested
explicitly. A semantic failure must remain visible; the upstream arithmetic
must not be silently rewritten or the input range narrowed to obtain a pass.

## Fixed scope and pending measurement declaration

Scan retains 4/8/12/16 Mi uint elements and one/three rotating input slots,
eight cells. Arms are the old internal baseline, fixed internal single-pass
candidate, upstream ReduceThenScan and upstream fallback scan.
Radix retains 1/4 Mi, keys/pairs, uniform/duplicate-heavy, and one/three slots,
16 cells. Arms are the fixed internal one-bit baseline, fixed internal eight-bit
candidate and official AMD four-bit Parallel Sort. No cell is removed because
the previous internal baseline was unstable.

Correctness uses uint32 exclusive sum modulo 2^32 and stable full-32-bit
ascending sorting with original-index uint32 payloads. Empty/partial/vector
and partition boundaries, overflow, duplicate/equal/extreme keys, and repeated
operation scratch initialization are separate correctness cases.

All arms will share one native D3D12 execution and measurement path. Immutable
input restoration required by destructive algorithms belongs to whole-operation
GPU timing. Upload, verification readback, CPU recording, CPU submission,
fence waiting, GPU operation and conversion-free GPU work are distinct fields.
GPU time is read from timestamps, never inferred from CPU waiting.

Formal five-process declarations, fresh seeds, balanced multi-arm order,
warmup, repetition counts and failure/stop policy must be committed before
formal sampling. Development data cannot be reused as confirmation. Failures
and inconclusive comparisons are valid outcomes; there are no retry-until-pass
runs or automatic default promotions.
