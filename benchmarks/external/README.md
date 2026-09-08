# External evaluation sources — prepared, Unmeasured

No benchmark, GPU dispatch, Player, calibration, profiling or counter collection
was executed for this repair. Builds do not establish GPU correctness or speed.

| Source | Pinned commit | Classification / scope |
|---|---|---|
| [GPUPrefixSums](https://github.com/b0nes164/GPUPrefixSums) | `98d93a4e9ed2f3c8353119515bf9be90a2e137ad` | Algorithm collection with native D3D12 tests/batches; MIT |
| [GPUSorting](https://github.com/b0nes164/GPUSorting) | `09a6081d964b682bbb58838b83ab75aebe7f4e05` | Sorting library with native D3D12 tests/batches; MIT |
| [FidelityFX SDK 1.1.4](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tree/c6efa6bf7f2027b3ec94f28578bb5965eabb9e55) | `c6efa6bf7f2027b3ec94f28578bb5965eabb9e55` | Official Parallel Sort component/sample and SDK backend; MIT |
| [Legacy ParallelSort](https://github.com/GPUOpen-Effects/FidelityFX-ParallelSort) | `0c539948c8d196ae338d91efbc8ca495f1ea0d1d` | Separate older official sample, not substituted for SDK 1.1.4 |
| [BabelStream](https://github.com/UoB-HPC/BabelStream) | `17ab377b0e919e14fd3df2b67268761fdac8abb3` | General memory bandwidth benchmark; STREAM-derived license/run rules |
| [HeCBench](https://github.com/zjin-lcf/HeCBench) | `7d2d3c567be522a2104065165de0a4a233a6ea1a` | Heterogeneous kernel suite; CUDA/HIP/SYCL/OpenMP, no native HLSL backend |

`third_party/upstream-lock.json` records byte lengths, SHA-256, Git blob SHA-1,
immutable URLs and retained licenses. The complete native D3D12 trees remain
byte-identical to upstream. `review-lock.json` records broader source review.
HeCBench radixsort files retain separate NVIDIA EULA notices; its root BSD
license is not assumed to override those terms. Kernel files are reference-only
and are not redistributed here.

No matched industry-standard cross-API scan/compaction/sort suite is claimed.
These native library benchmarks are external workloads with specific contracts.
UnifiedBench remains our shared-harness comparison, and Crowd/VFX our synthetic
application. Neither is renamed a standard benchmark. A timing framework alone
does not supply standard workloads.

## Native adapters

Local `native/` subclasses inherit original generators, validators, seeds,
batch methods and defaults. `upstream` delegates to the unchanged original main;
`candidate` substitutes our operation through a virtual recording hook.

- Scan retains `TestAll()` and `BatchTimingInclusiveInitOne(1 << 28, 100)`.
  Its batch is inclusive, so a local pass adds the input to our exclusive result
  inside the timing boundary. Reset, barriers and conversion stay included.
  Upstream random validation has a restricted domain and does not establish
  full-width wraparound; independent CPU models cover that separate contract.
- Sort retains ascending uint32 pairs, `TestAll()` and
  `BatchTiming(1 << 28, 100, 10, ENTROPY_PRESET_1)`. Interleave converts the
  original SoA input; final tiled scatter returns SoA. Conversion is included.
  Inherited DeviceRadixSort scan-subroutine tests still test its original
  prefix implementation. Its payload checks do not independently prove duplicate
  stability; our CPU models test original-order ties and arbitrary payloads.

The adapters add explicit COMMON/SRV/UAV transitions, scratch, wave32
requirements and candidate PSOs. Original utility allocations/PSOs remain.
Label future outputs **adapted upstream benchmark**, never original scores.
The 2^28 defaults and their large memory requirements are retained. No small
custom matrix replaces them. Native and borrowed-buffer SDK cost boundaries
are different.

Prepare/compile only with Visual Studio C++ tools (v143 default; use
`--toolset v145` for VS 2026):

```powershell
python tools/verify_external_sources.py
python tools/prepare_external_benchmarks.py --output .scratch/native --build --msbuild (Get-Command MSBuild.exe).Source
dotnet run --project tools/HlslPerf.CompileOnly -c Release -- . .scratch/sdk-compile
```

The preparer verifies three fixed NuGet packages and uses their headers/libs,
without package build hooks. Local host projects use C++17, current Windows SDK,
explicit WinRT link libraries and the legacy coroutine deprecation compatibility
macro. Logs and `preparation.json` retain identities. Candidate shader paths
refer to the source checkout, so moving it requires rebuilding. Builds and CI
never launch the produced programs.

Future entry points are `scan.exe upstream|candidate` and
`sort.exe upstream|candidate`, from each executable's directory. They fail
before device creation by default. Only after **new explicit user authorization**
may an owner set `HLSLPERF_EXECUTION_AUTHORIZATION=I_HAVE_NEW_USER_AUTHORIZATION`.
`HLSLPERF_GPU_POLICY=deny` overrides it. Never set authorization in builds or CI.
No future performance run is scheduled.

## Broader benchmark suitability

BabelStream's pinned default is 33,554,432 double elements and 100 iterations.
It supplies copy/mul/add/triad/nstream/dot with its own validation and run rules;
these memory kernels do not measure PCIe or global scan/sort. No HLSL port or
BabelStream result is claimed.

HeCBench scan performs independent block scans for integer widths and block
sizes 128–2048 (Makefile input 268,435,456 / 100), unlike global exclusive uint32
scan. Radixsort uses 4,194,304 full32 keys, repeat from the caller, and keys-only
semantics. Histogram uses image channels (default 1920×1080 / 100); its fixed
bandwidth reference is not collected hardware evidence. These CUDA/HIP programs
were reviewed only, not compiled or executed on the D3D12 stack. CUB and rocPRIM
are not claimed as native D3D12 backends.

## Historical evidence

The [prior shared-harness comparison](../../docs/integration/UNIFIED_BENCHMARK_RESULTS.md)
belongs to source `824cfafc9a07ade0a3cf440c1f5b3a19af2e8bf0` and its declared
R9700/driver. Frozen locks and data remain historical. Today's expanded lock
and new shaders do not inherit those results. New paths are **Unmeasured**.
