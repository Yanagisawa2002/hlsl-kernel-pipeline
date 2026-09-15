# Native inclusive wave-tiled scan

`PrimitiveOperations.InclusiveScan(assetRoot, input)` creates a complete, explicit
D3D12 operation with native inclusive uint32 output. For example, `[3, 1, 4]`
produces `[3, 4, 8]`. Addition wraps modulo 2^32: `[0xffffffff, 1, 2]` produces
`[0xffffffff, 0, 2]`. Every input element and every logical output is retained.

The change is a local extension of Thomas Smith's MIT-licensed GPUPrefixSums
scan/fallback adaptation, pinned at
`98d93a4e9ed2f3c8353119515bf9be90a2e137ad`. The upstream source, authorship and
complete license in `third_party/gpu-prefix-sums/LICENSE` are unchanged. The
contribution here is the inclusive output mode, SDK binding, and verification;
the underlying scan/fallback algorithm is credited to GPUPrefixSums.

## Application API

```csharp
uint[] input = [uint.MaxValue, 1, 2];
using var tuner = new D3D12Tuner("NVIDIA GeForce RTX 4090");
using var executor = tuner.CreateUnifiedExecutor();
var inclusive = PrimitiveOperations.InclusiveScan(assetRoot, input);
using var session = executor.Prepare(inclusive);
var checks = session.Verify(); // complete readback against the CPU oracle
if (checks.Any(c => !c.Passed)) throw new InvalidDataException("Scan mismatch");
byte[] output = session.ReadDiagnosticBuffer(inclusive.Outputs[0].Resource);

// Optional exclusive mode using the same wave-tiled implementation:
var exclusive = PrimitiveOperations.ExclusiveScan(assetRoot, input, ScanImplementation.WaveTiled);
// Omitting the enum still selects the established internal baseline.
```

These APIs construct plans from real caller input. Construction does not create
a device or measure performance. Both modes require SM6.6 and fixed wave32
support. They use separate shader IDs and can share one executor. The existing
`Select` policy can select wave-tiled **exclusive** only with explicit opt-in or
an exact trusted profile; its default remains the established baseline. The new
inclusive API is an explicit plan with no automatic fallback or deployment
profile. This experiment does not generate a profile or change defaults.

The SDK represents empty output with its existing four-byte zero sentinel and
copy guard; there is no scan dispatch or scratch reset. Logical output count
remains zero. This differs from the low-level kernel's empty-dispatch contract,
which preserves an existing output sentinel. Both behaviors are separately tested.

## Kernel and resource contract

Define `HLSLPERF_WAVE_TILED_INCLUSIVE=1` when compiling
`SinglePassScanWaveTiled` from `kernels/scan.hlsl`, together with the existing
wave-tiled defines. The default is zero (exclusive); values other than zero/one
and combining inclusive output with compaction are compile errors. The existing
exclusive/compaction workload builders reject inclusive candidate defines so
an exclusive oracle cannot accidentally be paired with inclusive output.
`WaveTiledScanCandidates.CreateInclusive(32)` supplies the explicit defines.
Wave64 is compiler/model checked; the public operation uses wave32.

The inclusive branch constructs
`[x, x+y, x+y+z, x+y+z+w] + tilePrefix` while values are in registers, then uses
the existing wave prefix, lookback and vector/tail stores. The exclusive branch
retains `[0, x, x+y, x+y+z] + tilePrefix`. Partition totals, publication order,
full-width state, barriers, fallback, ticket allocation and compaction are unchanged.
No extra input array or additional per-element storage is introduced.

For the fixed SDK candidate, group size is 256, wave size 32, items/thread 16,
partition size 4096, poll bound 4, and persistent group cap 256. Each nonempty
operation contains reset followed by scan, including host UAV barriers and
resource transitions. Scratch remains `8 + 12*ceil(N/4096)` bytes; input and
output each contain `4*N` bytes. Input, output and scratch do not alias. Queued
uses of the same scratch must be ordered; concurrent scans require separate
scratch. SDK buffer byte lengths use signed integers. Low-level raw buffers and
empty/tail semantics follow [the existing contract](SCAN_WAVE_TILED.md).

The removed native AddInput pass logically read both input/output and rewrote
output: `12*N` bytes, or 3 GiB at `N=2^28`, plus its dispatch/barriers. This is
source-level traffic accounting, not measured memory bandwidth. Reset and any
private-fallback reads remain in the fused operation.

## Correctness and frozen experiment

The targeted GPU runner exercises the public API and unchanged external RTS
adapter with independent full32 oracles, scalar/vector/partition tails, empty
cases, maximum values, nonuniform xorshift data and multiple partitions. Two
verification calls each poison all mutable buffers with `0xA5` and `0x5A`,
execute, compare every output byte and check immutable input. Separate cases
force missing-predecessor fallback without changing the shader, dispatch empty
scan/compaction kernels, and reuse one scratch allocation through shrinking and
growing counts with alternating modes and captured intermediate outputs.

The native host keeps `tile` as the old exclusive + AddInput arm and adds
`tile-fused`. Both inherit the same pinned RTS generator, `TestAll`, full-size
validator and `BatchTimingInclusiveInitOne(1 << 28, 100)` loop. The `rts` arm is
the unchanged upstream implementation in the adapted local host. All arms use
the same exact adapter and LUID. No small performance workload is substituted.

One exploratory process per arm is separate from formal confirmation. The
candidate is fixed before confirmation, with no parameter search. Six rounds
cover all six arm permutations, for 18 fresh processes. Each process performs
the original full-size validation before a batch with one excluded warmup and
100 measured iterations. The input is also exported once from the actual
upstream GPU generator and SHA-256 checked against independently constructed
all-one uint32 bytes.

GPU timestamps cover the complete scan recording hook: reset, all barriers,
scan, and AddInput for the legacy arm. Generation, compilation, allocation,
readback, CPU recording/submission and fence wait remain outside. These are GPU
operation times, not application end-to-end or Unity latency. Arithmetic means
and within-round geometric ratios use six independent process observations.
Nominal 95% intervals use Student-t, df=5, including intervals for paired time
differences. Inner iterations are not treated as independent processes.

## Reproduction on Windows

Requirements: .NET SDK from `global.json`, MSVC v143 (or explicitly selected
v145), Windows SDK, D3D12/SM6.6/wave32 GPU. Native dependencies are version/hash
locked by the preparer. Use new artifact directories on every attempt. All
build/compile/GPU commands below belong inside
`tools/Invoke-UnifiedValidationLock.ps1 -Action { ... }`, coordinating priority
with other tasks using `Local\CodexR9700VNextUnityGpu`.

```powershell
dotnet build tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --disable-build-servers
dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WaveTiledScanTests|FullyQualifiedName~PrimitiveOperationTests|FullyQualifiedName~WorkloadPackTests.Scan|FullyQualifiedName~WorkloadPackTests.CompactionExposesUnfusedAndFusedEndToEndPlans'
python tests/test_wave_tiled_scan.py
python tools/test_external_contracts.py
python tools/test_inclusive_scan_analysis.py
python tools/verify_external_sources.py
python tools/prepare_external_benchmarks.py --output .scratch/native --programs scan --build --msbuild 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe'
python tools/check_wave_tiled_scan.py --dxc .scratch/native/packages/Microsoft.Direct3D.DXC/build/native/bin/x64/dxc.exe --output .scratch/compile
python tools/check_inclusive_control.py --dxc .scratch/native/packages/Microsoft.Direct3D.DXC/build/native/bin/x64/dxc.exe --output .scratch/control
dotnet build tools/HlslPerf.InclusiveScanValidation -c Release --disable-build-servers
dotnet tools/HlslPerf.InclusiveScanValidation/bin/Release/net10.0/HlslPerf.InclusiveScanValidation.dll . 'NVIDIA GeForce RTX 4090' .scratch/managed
python tools/run_inclusive_scan.py probe --native .scratch/native --output .scratch/probe
python tools/run_inclusive_scan.py validate --native .scratch/native --device .scratch/probe/device.json --output .scratch/validation
python tools/run_inclusive_scan.py discover --native .scratch/native --device .scratch/probe/device.json --output .scratch/discovery
python tools/run_inclusive_scan.py freeze --native .scratch/native --device .scratch/probe/device.json --validation .scratch/validation --managed .scratch/managed/correctness.json --discovery .scratch/discovery --output .scratch/registration
python tools/run_inclusive_scan.py confirm --native .scratch/native --device .scratch/probe/device.json --plan .scratch/registration/plan.json --output .scratch/confirmation
python tools/run_inclusive_scan.py analyze --confirmation .scratch/confirmation --output .scratch/analysis.json
```

For another adapter, pass its exact DXGI name to `probe --adapter` and the
managed runner. Device/driver identity is then frozen and checked in every
process. The runtime source retains the old R9700-only contract for historical
callers that omit the adapter argument. Historical September 9 R9700 numbers
are not combined with the new machine's results.
