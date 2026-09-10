# Application counts and draw order through RTS / AMD

This small application builds reusable GPU operation plans from five visible-item
counts and five material-key/draw-ID pairs. It explicitly chooses the existing
GPUPrefixSums ReduceThenScan and AMD Parallel Sort SDK adapters. The console
entry point runs on the CPU only; it neither creates a device nor compiles HLSL,
submits GPU work, times anything or writes a deployment profile.

## Build and inspect on the CPU

From the repository root, use .NET 10 and the already cached dependencies:

```powershell
dotnet restore examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj --configfile examples/HlslPerf.PrimitiveApp/NuGet.offline.config -p:NuGetAudit=false
dotnet build examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj -c Release --no-restore
dotnet run --project examples/HlslPerf.PrimitiveApp -c Release --no-build --no-restore -- . --check
```

The offline configuration has no package sources: missing dependencies fail
instead of downloading. For a prepared worktree with current Release dependency
outputs, the smaller incremental build used on September 10 was:

```powershell
dotnet build examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj -c Release --no-restore -p:BuildProjectReferences=false -p:CopyLocalLockFileAssemblies=false
```

That build resolves managed D3D12/Vortice references but avoids copying native
compiler packages. It suffices for this CPU entry point; an application executing
the GPU method needs its normal runtime dependencies. Do not disable dependency
builds in a fresh checkout or after changing a referenced project.

[Program.cs](Program.cs) is the complete runnable example. Its input and oracle
metadata represent an application draw batch:

| Input | Expected output contract |
|---|---|
| Visible counts `[3, 0, 2, 4, 1]` | Exclusive offsets `[0, 3, 3, 5, 9]`, uint32 sum modulo `2^32` |
| Material keys `[0x80000000, 7, 7, 0, uint.MaxValue]` and draw IDs `[1001, 42, 9007, 18, 73]` | Sorted keys `[0, 7, 7, 0x80000000, uint.MaxValue]`, IDs `[18, 42, 9007, 1001, 73]`; equal keys retain input order |

The key selection calls are ordinary public API calls:

```csharp
var requested = PrimitiveOperations.StableSort(root, materialKeys, drawIds,
    SortImplementation.AmdParallelSort);
var fallback = PrimitiveOperations.StableSort(root, materialKeys, drawIds);
var selected = PrimitiveOperations.Select(root, requested, fallback, runtime,
    allowUnmeasured: true);
// Allocate, compile and record selected.Plan, including when UsedFallback is true.
```

`allowUnmeasured` expresses application policy to use a supported, source-verified
implementation without a speed claim. It is not chat authorization and performs
no automatic tuning. `Main` supplies an explicitly synthetic runtime only to
exercise CPU planning; production code must supply independently observed device,
driver, shader-model/wave support and the hash of its actual compiler. Never
copy runtime identity or a trusted confirmation hash out of an incoming profile.

## Support, source and identity

RTS uses GPUPrefixSums commit `98d93a4e9ed2f3c8353119515bf9be90a2e137ad`,
D3D12/SM 6.7, with upstream wave selection. AMD uses FidelityFX SDK 1.1.4 commit
`c6efa6bf7f2027b3ec94f28578bb5965eabb9e55`, D3D12/SM 6.6 and wave64. Source
files, byte lengths and hashes come from
[the pinned source lock](../../third_party/upstream-lock.json); distribute the
complete includes and upstream license notices with the assets. This is the
existing host scheduling adapter over upstream shaders, not the full FidelityFX
host runtime. DeviceRadixSort, OneSweep and GPUSorting's vendored FFX are separate
native-harness implementations, not additional public enum options.

`Select` checks that requested and fallback plans preserve the same input and
full output contract. It rejects an incompatible fallback instead of pretending
one exists. The scan baseline supports SM 6.6. The binary sort baseline requires
wave32 for keys-only and wave64 for pairs; thus a wave32-only device cannot use
either of this example's pair-sort plans. The caller must handle that failure
with another application path. The keys-only CPU check demonstrates a supported
wave32 fallback when AMD is unavailable.

Absent a profile, the default policy returns the explicit internal baseline;
the sample opts into each external implementation. A supplied mismatched profile
still falls back even with `allowUnmeasured: true`. Confirmation must match the
ABI, implementation, source/include bytes, compile options, complete plan,
input/oracle hashes, Core assembly and runtime, plus an independently supplied
trusted confirmation hash. Changed application data needs a new plan and cannot
inherit a profile for another input. Source revision/byte failures, unreadable
assets, invalid JSON or malformed lock structure select the verified baseline
with a diagnostic. Empty file lists are rejected. Invalid input/fallback
contracts still throw.

The 12 `--check` assertions cover the two explicit selections, unmeasured status,
application oracle metadata, complete restore/trim stages, absent/stale/untrusted
profile fallback, unsupported shader model/wave fallback and empty sentinels.
The [targeted regression tests](../../tests/HlslPerf.Core.Tests/PrimitiveOperationTests.cs)
also inject 12 source faults: revision, bytes, missing file, JSON syntax and eight
structural failures involving missing fields, array types, missing/duplicate
sources, empty file lists and null paths. They use newly created temporary copies;
no test mutates the checkout's pinned sources.

## Recording and ownership in an application

[ApplicationRecording.cs](ApplicationRecording.cs) compiles against the real
`D3D12OperationRecorder` and Vortice types. Its `RecordAndSubmit` method performs
actual recording, queue execution and signaling when called by a Windows/D3D12
application. `Main` never calls it. No dummy device or simulated GPU is used.

1. Build both plans and select **before** allocating or choosing PSOs. Use
   `selection.Plan` even after fallback. Compile every `plan.Shaders` entry with
   its declared entry point, SM, HLSL version, defines, includes and arguments;
   match the unified root signature (two SRVs, five UAVs, eight constants).
   `D3D12OperationRecorder.CreateRootSignature(device)` supplies that signature;
   strip embedded root signatures as in the existing
   [compile-only tool](../../tools/HlslPerf.CompileOnly/Program.cs).
2. The app allocates every named buffer at least `ByteLength` bytes, UAV-capable
   and distinct, plus a distinct UAV-capable dummy of at least 256 bytes in
   COMMON. Upload **all** non-null `InitialData` before the recorded operation,
   ordered on the same queue or through an explicit queue dependency. Keep
   `ImmutableInputs` unchanged between repeated invocations. Upload staging,
   root signature, PSOs, buffers and dummy remain application-owned.
3. The caller checks `CanReuse` **before** resetting its allocator or overwriting
   prior resources, supplies an open command list and an accurate shared state
   map, and issues a fresh value on its exclusively managed monotonic queue fence.
   `RecordAndSubmit` repeats the completion guard, records all plan passes, closes
   and executes the list, signals that fence and returns the value.
4. Retain the allocator, command list and all referenced resources/PSOs through
   completion. The returned ticket covers this submission; subsequent output
   consumers/readbacks need their own ordering and a fence covering their final
   use before disposal. No per-call allocation or wait is hidden in the recorder.
   The application can poll or wait using its normal frame scheduling policy.
5. Account for buffer decay at `ExecuteCommandLists` boundaries and all external
   transitions when preparing the next state map. Resynchronize after abandoning
   a recorded list. If signaling fails after execution, work may still be in
   flight: follow queue/device recovery, not immediate disposal. A removed-device
   fence value is treated as failure, not successful completion.

The sample targets ordered reuse on one application queue; a multi-queue or
overlapped frame allocator needs application synchronization. The recorder does
not validate that an arbitrary PSO was compiled from the declared source, nor
does identity matching itself prove GPU correctness. Those remain integration
and validation responsibilities.

## Complete cost boundary

The reported sizes are logical buffer bytes, not heap allocation or VRAM usage:
the five-count RTS plan uses 88 bytes and the five-pair AMD plan uses 248 bytes,
each excluding dummy, alignment and driver overhead. These tiny inputs illustrate
contracts, not a useful performance comparison.

| Phase | What an application must account for |
|---|---|
| CPU preparation | Plan construction for requested and fallback paths, copies of input, CPU oracle generation (including sorting), source/include hashing, support/profile checks; cache/reuse only at the correct identity |
| Setup | HLSL compilation, PSO/root creation, actual aligned allocation, upload heaps and transfers, queue dependencies; account separately or amortize with the reuse count declared |
| Complete GPU operation | Every `InputRestore`, `ScratchInitialization`, `Algorithm`, `OutputConversion` pass and every transition/UAV barrier; RTS has three algorithm passes plus padding trim here; AMD includes input restore/deinterleave and all 40 radix passes |
| CPU submission | Command recording, close/execute/signal, allocator reset and any queue/fence wait; GPU timestamps around dispatches do not include these costs |
| Application consumption / validation | Output conversion required by the consumer, readback/copies and fence waits if needed, complete key/payload oracles, repeated-execution checks, and any final consumer work |

The borrowed recorder adds no timestamps, readback or benchmark harness. When
measuring, distinguish GPU operation time from end-to-end application latency
and report setup/reuse, host work, synchronization and validation separately.
Do not drop restore, conversion or scratch costs to compare kernels favorably.

This new source has no GPU execution, timing, Unity or deployment-profile result.
The [seven September 9 native comparisons](../../docs/results/R9700_NATIVE_CONFIRMATION_2026-09-09.md)
remain historical: only tile4 beat GPUSorting FFX at its `2^25` workload; the
other six candidates were slower. Neither that one win nor the six losses select
a default for this application. No historical win rate transfers to this example.
