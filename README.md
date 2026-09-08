# HlslKernelPipeline

An independent, engine-neutral HLSL kernel autotuning and evidence pipeline for
Windows/D3D12. It executes real algorithms, rejects incorrect variants, measures
complete GPU execution plans, attaches optional AMD RGA evidence, and emits a
device-specific profile that Unity can consume without importing the tuner.

This is clean-room personal work. It contains no employer/client project source,
assets, configuration, benchmark capture, or Git history.

The [dynamic ABI v2 demo](docs/DYNAMIC_EXECUTION.md) adds bounded GPU-count indirect
dispatch and independent multi-output verification while retaining ABI v1.
Its R9700 correctness evidence is separate from the historical performance results below.

## What this project demonstrates

GPU kernel choices depend on the complete operation, workload and device. This
pipeline turns those choices into reproducible measurements and a compatible
profile that a consumer can load.

- **Algorithms:** scan, reduction, stable radix sort, segmented scan, fused
  compaction, histogram offsets and transpose in HLSL.
- **Systems:** engine-neutral workload plugins, a raw-buffer execution ABI,
  D3D12 dispatch/barrier/timestamp execution, and source-aware compilation caches.
- **Validation:** CPU oracles, output poisoning and re-execution, randomized
  paired measurements, independent confirmation and retained failed gates.
- **Integration:** device-specific profiles and a read-only Unity consumer.

## Latest confirmed result and limits

The [September 7 vNext integration report](docs/results/R9700_VNEXT_INTEGRATION_2026-09-07.md)
retained all 18 runs and passed 19,584 output/poison checks. At 16M elements,
single-pass scan confirmed **1.4782x** and **1.5254x** speedups for one and three
resident slots. Wide-radix and dynamic-plan results did not establish deployable
gains, and no runtime defaults were promoted.

These are bounded AMD Radeon AI PRO R9700 / D3D12 measurements under the report's
declared controls, not universal gains or wins over every external library.
The separate [external comparison](docs/integration/UNIFIED_BENCHMARK_RESULTS.md)
retains inconclusive and semantically incompatible comparisons as well as wins.

## Review and reproduce

1. Read the [SDK contract](docs/SDK.md) and architecture below for implementation scope.
2. Follow [Quick start](#quick-start) to build and run a small workload.
3. Use the [vNext replay instructions](docs/integration/REPLAY.md) for the reported protocol.
4. Inspect the [experiment history and visual showcases](docs/EXPERIMENT_HISTORY.md)
   for earlier results, exact baselines, source reports and negative findings.

## Architecture and ownership boundary

    manifest v3 + workload provider/plugin + HLSL/include graph
                    |
                    v
          engine-neutral execution plan
       buffers + t0/t1/u0/u1 + b0[8] + passes
                    |
                    v
           generic D3D12 plan executor
       upload -> dispatch/barriers -> timestamps
                    |
          +---------+----------------+
          |                          |
          v                          v
      CPU oracle              optional RGA adapter
      SHA-256 gate      VGPR/SGPR/LDS/scratch/live VGPR
          |                          |
          +-------------+------------+
                        v
          run.json + CSV + HTML/SVG/GIF
                        |
                        v
              selected profile.json
                        |
                        v
      independent Unity UPM consumer (read-only)

The core and workload pack reference no Unity assemblies. The package under
`unity/com.edwinliu.hlslperf-profile` only validates and resolves an emitted
profile. Project code owns the final mapping from integer defines to its shader
variant system through `IHlslPerfDefineSink`.

## Real workload pack

- `reduction-u32-v1`: recursively reduces all input elements to one uint.
- `exclusive-scan-u32-v1`: compares hierarchical Blelloch, wave-hybrid, and
  persistent decoupled-look-back single-pass backends.
- `generic-exclusive-scan-u32-v1`: add/min/max/XOR with Wave32/64 and vector axes.
- `segmented-exclusive-scan-u32-v1`: pair-monoid segmented look-back scan.
- `stream-compaction-u32-v1`: unfused scan/scatter versus fused producer-consumer.
- `histogram-prefix-u32-v1`: global atomics versus replicated LDS plus offsets.
- `radix-sort-u32-v1`: stable 32-bit binary LSD sort using reusable scan scratch.
- `transpose-u32-v1`: padded groupshared 2D tiles with tunable tile dimension
  and block-row count.

Each provider builds the same public `KernelExecutionPlan` ABI, so a future
external workload package can supply buffers, constants, passes, dispatch
dimensions, and an oracle without changing the D3D12 backend.

## Quick start

Requirements: Windows 10/11, a D3D12-capable GPU, and .NET 10 SDK.

    dotnet build HlslKernelPipeline.slnx -c Release
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/reduction.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/scan.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/scan-single-pass.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/scan-generic.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/segmented-scan.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/compaction.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/histogram-prefix.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/radix-sort.json
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/transpose.json
    dotnet run --project src/HlslPerf.Showcase -c Release --no-build
    dotnet run --project src/HlslPerf.Showcase -c Release --no-build -- --stress-grid --budget-ms 8.333333
    dotnet run --project src/HlslPerf.GpuDrivenDemo -c Release --no-build -- validate

The Crowd/VFX GPU matrix is intentionally a separate explicit action:

    dotnet run --project src/HlslPerf.GpuDrivenDemo -c Release --no-build -- run --level all --budget-ms 8.333333

RGA is optional and never redistributed. If its CLI is installed or unpacked,
attach live-driver evidence with:

    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/scan.json --rga C:/tools/rga/rga.exe --rga-target gfx1201

Use `--rga off` to disable auto-discovery. Without RGA, correctness, timing,
selection, and profile emission still work.

Outputs go to `.hlslperf/runs/<timestamp>/`. GPU timing is never cached; only
DXIL compilation is cached by source/compiler/options/entry-point/define
identity. The profile compatibility key includes device, driver, backend,
shader model, compiler, manifest hash, and transitive kernel hash. After timing,
the runner poisons the verified resource and requires one already-used plan
invocation to reconstruct every byte before a profile can be emitted.

The standalone showcase under `showcase/` references the public execution ABI
and D3D12 backend, not Unity. Unity remains a read-only profile consumer.

## Deliberate non-goals

- No Unity project or employer repository is required by the tuner.
- No replacement compiler, GPU API binding, ISA analyzer, or generic search
  framework is implemented here; DXC, Vortice, and optional RGA are reused.
- RGA static resource data does not replace measured GPU timestamps.
- Current RGA DX12 text output does not provide total theoretical occupancy.
  The profile keeps that field nullable instead of fabricating it from VGPRs;
  full occupancy evidence belongs to an RGP/runtime-counter adapter.
- Exhaustive enumeration is intentional for small candidate spaces. A search
  strategy adapter becomes useful only when a real workload exceeds that scale.

Read the [ABI contract](docs/ABI.md), [measurement methodology](docs/METHODOLOGY.md),
[RGA evidence policy](docs/RGA.md), [Unity consumer boundary](docs/UNITY_ADAPTER.md),
[SDK guide](docs/SDK.md), [roadmap](docs/ROADMAP.md), and
[clean-room provenance](docs/PROVENANCE.md).

## Benchmark reproduction permission

The [limited benchmark reproduction permission](LICENSE.md#limited-benchmark-reproduction-permission)
allows anyone to run the benchmarks and required project components, make local
changes needed for reproduction, and publish measurement results. Other plugin
rights remain reserved; this is not an MIT or general open-source license.
