# HlslPerf SDK 0.6

HlslPerf is split into packages so a workload can live in its own repository and
the engine integration never needs to own the tuner.

| Package | Responsibility |
|---|---|
| `EdwinLiu.HlslPerf.Core` | public execution ABI, manifest model, plugin contract, schemas, statistics, reports |
| `EdwinLiu.HlslPerf.Workloads` | reusable primitive workload pack and CPU oracles |
| `EdwinLiu.HlslPerf.D3D12` | Windows/D3D12 execution and timestamp backend |
| `EdwinLiu.HlslPerf.Rga` | optional AMD RGA static-evidence adapter |
| `EdwinLiu.HlslPerf.Cli` | `hlslperf` .NET tool that composes the packages |

The SDK contains no Unity references. The UPM package is a separate read-only
consumer of a selected profile.

## Install and scaffold

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
- deployable profile 2.0;
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
