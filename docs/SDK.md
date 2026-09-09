# HlslPerf SDK 0.6

HlslPerf exposes reusable GPU primitive execution and explicit backend selection.
Applications supply their data and own GPU resources; trustworthy measurement
can inform a later deployment choice. Internal algorithms are research
candidates alongside mature upstream implementations.

## First use: application data to an explicit plan

Start with [HlslPerf.PrimitiveApp](../examples/HlslPerf.PrimitiveApp/README.md),
a compilable example using only public SDK APIs. Five visible-item counts become
an RTS exclusive-scan plan; full-width material keys and arbitrary draw IDs become
an AMD stable-sort plan. `Main` performs CPU work only, reports complete pass
stages and source/plan identities, and can check support and profile fallback.
The separately compiled `ApplicationRecording.RecordAndSubmit` demonstrates
borrowing an application's resources and returning its completion-fence ticket.

From the repository root, with .NET 10 and dependencies already cached:

```powershell
dotnet restore examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj --configfile examples/HlslPerf.PrimitiveApp/NuGet.offline.config -p:NuGetAudit=false
dotnet build examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj -c Release --no-restore
dotnet run --project examples/HlslPerf.PrimitiveApp -c Release --no-build --no-restore -- . --check
```

The offline configuration prevents dependency downloads and fails if a needed
package is absent. No packaging, tuning or GPU is needed for this first use.
The example's runtime capabilities are explicitly a CPU fixture; a real caller
must obtain its own device, driver, compiler and support identity. Successful
planning does not verify GPU output or establish an application speedup.

## Packages

The packages also allow workload plugins to live in their own repositories.

| Package | Responsibility |
|---|---|
| `EdwinLiu.HlslPerf.Core` | public execution ABI, manifest model, plugin contract, schemas, statistics, reports |
| `EdwinLiu.HlslPerf.Workloads` | reusable primitive workload pack and CPU oracles |
| `EdwinLiu.HlslPerf.D3D12` | Windows/D3D12 execution and timestamp backend |
| `EdwinLiu.HlslPerf.Rga` | optional AMD RGA static-evidence adapter |
| `EdwinLiu.HlslPerf.Cli` | `hlslperf` .NET tool that composes the packages |

`HlslPerf.GpuDriven` is a standalone example of the external plugin boundary:
it supplies a complete Crowd/VFX application workload without adding that
application to the core or built-in primitive package.

The SDK contains no Unity references. The UPM package is a separate read-only
consumer of a selected profile.

## Explicit operations and external backends

`PrimitiveOperations.ExclusiveScan(root, input, implementation)` and
`PrimitiveOperations.StableSort(root, keys, payloads, implementation)` construct
CPU-side plans from application input. Payloads are arbitrary uint32 values;
keys are full-width, ties preserve original order. Empty operations regenerate
four-byte zero sentinels. Defaults remain the internal scan/binary radix
baselines. GPUPrefixSums ReduceThenScan and AMD Parallel Sort are explicit enum
options. Packed-flag fallback scan is excluded from this full-width SDK API.

| Public option / surface | Current support and provenance | Evidence boundary |
|---|---|---|
| `ScanImplementation.GpuPrefixSumsReduceThenScan` | D3D12, SM 6.7; GPUPrefixSums `98d93a4e9ed2f3c8353119515bf9be90a2e137ad` | Existing SDK adapter; current application plan has no confirmed deployment profile |
| `SortImplementation.AmdParallelSort` | D3D12, SM 6.6, wave64; FidelityFX SDK 1.1.4 `c6efa6bf7f2027b3ec94f28578bb5965eabb9e55` | Existing SDK adapter, including full32 arbitrary payloads; distinct from GPUSorting's vendored FFX |
| DeviceRadixSort / OneSweep / GPUSorting FFX | Native evaluation harness only | September 9 native results, not `PrimitiveOperations` options |
| Wave-tiled scan/compaction and tiled 4/8-bit sort | Explicit research/consumer or harness paths | Tested September 9 cases retain their exact evidence; no SDK default promotion |

Capability checks evaluate both plans. The scan baseline supports SM 6.6;
the binary sort baseline uses wave32 for keys-only and wave64 for pairs.
An unsupported external option falls back only if that particular baseline is
supported. If neither is supported, `Select` throws; callers must choose a
different application path. There is no implicit CPU or cross-API fallback.

`PrimitiveOperations.Select` validates identical input/output contracts,
capabilities, pinned source files and exact source/plan/ABI/runtime identity.
An application supplies a trusted confirmation hash independently of an incoming
`OperationDeploymentProfile`. Missing or mismatched profiles choose the explicit
baseline; `allowUnmeasured: true` permits a supported alternative with no
performance claim. A mismatched supplied profile still falls back. Shader model,
wave range, device, driver and compiler identities come from the application.
Historical tuning/Unity profiles are not implicitly converted into this profile.
Pinned source revision/byte mismatches, unreadable files and malformed JSON now
return the validated baseline with a diagnostic instead of escaping selection.
Input and expected-output hashes are part of the exact operation identity:
changing the input requires a new plan, and a confirmed profile for different
data cannot be reused as a general winner policy. Identity validation is not
independent confirmation itself.

`D3D12OperationRecorder` records a plan against application-owned resources,
states, root signature and precompiled PSOs. Allocate and upload InitialData
before recording, preserve the input for repeated calls, and keep all resources
alive through the caller's completion fence. It records copies, dispatches,
transitions and UAV barriers without a tuner, timestamps, submission or readback.
Provide distinct named UAV-capable buffers and a 256-byte dummy buffer initially
in COMMON state. The mutable state map tracks recorded transitions; resynchronize
it when abandoning a command list or using those resources elsewhere. This ABI
uses two SRVs/five UAVs/eight constants and has its own
`hlslperf.unified-operation.v1` identity; it is not ABI-v1 Unity shader mapping.

Build and CPU validation are separate from recording/execution. Existing GPU
APIs and CLI commands remain normal runnable product features, with application
policy expressed through API arguments rather than chat authorization. The
September 10 integration work invokes only the compile/CPU paths; CI uses an
explicit allowlist and never runs the
GPU correctness or benchmark programs. See
[external evaluation](../benchmarks/external/README.md).

The workload package now includes the complete shared shader set and pinned
third-party sources/licenses under `contentFiles/any/any/hlslperf`. Use that
asset root when building operation plans. Do not distribute just the thin root
shader without its includes or third-party notices.

For a real Unity scan consumer, `tools/export_scan_consumer.py --output <new-dir>`
copies `ScanWaveTiled.compute`, its actual independent shared header and project
license with a sorted per-file SHA-256 manifest. Variant identity is
`hlslperf.scan-wave-tiled.u32.wave32.g256.b4096.v1`, ABI `hlslperf.raw-buffer.v1`.
This Raw-buffer, D3D12 wave32 path uses B=4096, reset plus scan, preallocated
`8 + 12*ceil(capacity/4096)` scratch, no aliases and ordered same-queue use.
Count zero skips dispatch and preserves the sentinel. The September 9 report
contains standalone GPU checks for its recorded kernel/empty-host cases, but
provides no Unity import, Player measurement or deployment profile for this
consumer. The current application example also inherits none of those results.
SUMMIT owns its explicit consumer recorder and fallback. The fused compaction mask contract
does not match SUMMIT's independent nonzero predicate and is not advertised as
integrated there.

## Optional plugin authoring: install and scaffold

Packages can be built locally without publishing them:

    dotnet pack HlslKernelPipeline.slnx -c Release -o artifacts/packages
    dotnet tool install EdwinLiu.HlslPerf.Cli --version 0.6.0 --tool-path .tools --add-source artifacts/packages
    .tools/hlslperf new-workload ../MyGpuWorkload --id my-workload-v1 --class MyGpuWorkload

The generated project implements `IKernelWorkloadProvider`, contains a real
copy-kernel oracle, and builds outside this repository. Load one assembly or
discover all top-level assemblies in a directory:

    hlslperf workloads --plugin path/to/MyGpuWorkload.Workload.dll
    hlslperf tune manifest.json --plugin path/to/MyGpuWorkload.Workload.dll
    hlslperf tune manifest.json --plugin-dir path/to/plugins

An explicit `--plugin` DLL must contain a provider. Directory discovery ignores
adjacent managed or native dependency DLLs that do not export one.

Plugin assemblies execute normal .NET code in-process. They are a trusted-code
extension boundary, not a sandbox; do not load an untrusted DLL.

## Workload contract

A provider exports one or more stable workload ids. Its workload converts a
manifest and candidate defines into a `KernelExecutionPlan`:

- exact named raw buffers and deterministic initial bytes;
- a sequence of entry points, dispatch sizes, bindings, and up to eight root
  constants per pass;
- a logical item count;
- one fully deterministic verified resource and its independent CPU-oracle
  SHA-256.

The runner times the complete pass sequence. After timing, it overwrites the
entire verified resource with `0xA5`, runs the plan once more on the already-used
state, and only then hashes it. A workload therefore has to regenerate every
verified byte on every invocation; a correct first run followed by stale or
empty iterations cannot pass.

See [Kernel ABI v1](ABI.md) for the binding-level contract.

## Manifest schema 3.0

An axis can exist only when earlier axes have selected particular values. An
implication constraint removes semantically invalid products:

```json
{
  "schemaVersion": "3.0",
  "axes": [
    { "name": "BACKEND", "values": [1, 2, 3] },
    { "name": "ELEMENTS_PER_THREAD", "values": [1, 4] },
    { "name": "VECTOR_WIDTH", "values": [1, 4] },
    {
      "name": "PERSISTENT_GROUPS",
      "values": [64, 128, 256],
      "when": { "BACKEND": [3] }
    }
  ],
  "constraints": [
    {
      "if": { "VECTOR_WIDTH": [4] },
      "then": { "ELEMENTS_PER_THREAD": [4] }
    }
  ]
}
```

Conditions may reference fixed defines and earlier axes. Constraints may
reference any define. Candidate ids and the DXIL cache contain only active axes,
so a hierarchical backend does not acquire meaningless persistent-group values.

The public schemas are immutable, versioned files under `schemas/` and are also
packed inside `EdwinLiu.HlslPerf.Core`:

- manifest 3.0;
- deployable paired profile 3.0 (historical 2.0 requires explicit opt-in);
- checkpoint `hlslperf.checkpoint.v1`.

## Shared HLSL and source identity

`kernels/include/hlslperf/scan_u32.hlsli` is the reusable scan library. A kernel
hash covers the root HLSL file plus every transitive quoted or angle-bracket
include, using normalized relative paths and content hashes. Include cycles are
rejected. The same combined hash keys DXIL cache entries, checkpoints, emitted
profiles, and Unity compatibility checks.

Changing a shared `.hlsli` therefore invalidates old binaries and profiles even
when the thin root kernel file is unchanged.

## Checkpoint and resume

Every tune command writes a candidate-granular checkpoint by default:

    hlslperf tune manifests/scan-generic.json --checkpoint runs/scan/checkpoint.json
    hlslperf tune manifests/scan-generic.json --resume runs/scan/checkpoint.json

Resume accepts a candidate only when manifest hash, transitive kernel hash,
complete device/driver/compiler fingerprint, workload id, workload implementation
assembly/type hash, ABI id, and candidate space match. It also records SDK and
measurement-protocol versions, so a rebuilt plugin or pre-gate checkpoint cannot
bypass a newer correctness protocol. GPU timings are never taken from the DXIL
cache; a checkpoint is an explicit, auditable reuse mechanism.

## Release process

The repository can always produce local `.nupkg` files. The GitHub release
workflow builds and tests on Windows, uploads the packages as a workflow
artifact, and publishes tag builds only when the repository owner has configured
`NUGET_API_KEY`. Merely adding the workflow does not claim that 0.6.0 has been
published to nuget.org.
