# HlslKernelPipeline

An independent, engine-neutral HLSL kernel autotuning and evidence pipeline for
Windows/D3D12. It executes real algorithms, rejects incorrect variants, measures
complete GPU execution plans, attaches optional AMD RGA evidence, and emits a
device-specific profile that Unity can consume without importing the tuner.

This is clean-room personal work. It contains no employer/client project source,
assets, configuration, benchmark capture, or Git history.

## v0.2 result

![R9700 scan candidate comparison](docs/results/r9700-v02-scan.gif)

All 37 candidates across full reduction, full exclusive scan, and tiled matrix
transpose passed byte-for-byte CPU oracles on an AMD Radeon AI PRO R9700. All 37
also met the stability budget and produced RGA 2.14.2 `gfx1201` evidence.

| Workload | Selected compile-time variant | Median | P95 | vs baseline |
|---|---|---:|---:|---:|
| 4M uint reduction | group 512, 8 items/thread | 0.011579 ms | 0.012037 ms | 1.1023x |
| 4M uint exclusive scan | group 128, 2 items/thread | 0.046190 ms | 0.046356 ms | 1.1186x |
| 2048x2048 uint transpose | tile 32, 8 block rows | 0.020756 ms | 0.020798 ms | 1.2493x |

These are cache-warm steady-state results for this GPU/driver/compiler and are
not universal parameter recommendations. See the [full R9700 v0.2 evidence](docs/results/R9700_WORKLOAD_PACK_2026-09-01.md).

## Architecture and ownership boundary

    manifest v2 + workload provider + HLSL
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
- `exclusive-scan-u32-v1`: scans block totals recursively, then propagates
  offsets back down every level to produce a global exclusive scan.
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
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/transpose.json

RGA is optional and never redistributed. If its CLI is installed or unpacked,
attach live-driver evidence with:

    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/scan.json --rga C:/tools/rga/rga.exe --rga-target gfx1201

Use `--rga off` to disable auto-discovery. Without RGA, correctness, timing,
selection, and profile emission still work.

Outputs go to `.hlslperf/runs/<timestamp>/`. GPU timing is never cached; only
DXIL compilation is cached by source/compiler/options/entry-point/define
identity. The profile compatibility key includes device, driver, backend,
shader model, compiler, manifest hash, and kernel hash.

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
and [clean-room provenance](docs/PROVENANCE.md).
