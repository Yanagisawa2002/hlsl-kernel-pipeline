# HlslKernelPipeline

An independent, engine-neutral HLSL kernel autotuning and evidence pipeline for
Windows/D3D12. It executes real algorithms, rejects incorrect variants, measures
complete GPU execution plans, attaches optional AMD RGA evidence, and emits a
device-specific profile that Unity can consume without importing the tuner.

This is clean-room personal work. It contains no employer/client project source,
assets, configuration, benchmark capture, or Git history.

## v0.4 persistent single-pass and budget crossing

![R9700 measured 120 Hz budget crossing](docs/results/r9700-single-pass-budget-crossing.gif)

The new scan backend combines persistent logical-block claiming, wave-local
prefixes, and decoupled look-back. The image above is not a chart animation:
both sides use byte-identical GPU-generated frame atlases, while frame advance
replays the recorded per-plan GPU samples against a real 8.3333 ms deadline.
No delay or quality difference is synthesized.

At eight scans per plan, the selected backend pivots from Wave at 2M elements to
single-pass at 4M and then gains as the working set grows:

| Elements | Selected backend | Baseline median | Selected median | Selected p95 | vs baseline |
|---:|---|---:|---:|---:|---:|
| 2M | Wave, group 128, EPT 1 | 0.3515 ms | 0.2996 ms | 0.3031 ms | 1.1731x |
| 4M | Single-pass, group 512, IPT 8 | 0.5317 ms | 0.4413 ms | 0.4442 ms | 1.2047x |
| 8M | Single-pass, group 512, IPT 8 | 0.8929 ms | 0.7175 ms | 0.7218 ms | 1.2443x |
| 12M | Single-pass, group 512, IPT 8 | 2.1058 ms | 1.5257 ms | 1.5389 ms | 1.3802x |
| 16M | Single-pass, group 512, IPT 8 | 3.7291 ms | 1.9582 ms | 1.9625 ms | 1.9044x |
| 24M | Single-pass, group 512, IPT 8 | 5.7465 ms | 2.8541 ms | 2.8633 ms | 2.0134x |

The strongest validated 120 Hz crossing was 24M elements × 16 scans: baseline
11.3955 ms / p95 11.5060 ms versus single-pass 5.6151 ms / p95 5.6525 ms,
or 2.0294x. Across the 46-point stress grid, all 1,656 candidate outputs passed
the oracle and every baseline/selected pair passed the stability gate. The
standalone 4M scan also selected single-pass at 0.04316 ms, 1.2122x over its
declared baseline.

See the [single-pass design, full evidence, and limitations](docs/results/R9700_SINGLE_PASS_2026-09-02.md).

## v0.3 actual-scene A/B showcase

![R9700 extreme-pressure scan particle A/B](docs/results/r9700-scan-particles-extreme-ab.gif)

This is the requested picture-level comparison, not a chart animation. Baseline
and tuned kernels separately generate the same 24-frame particle-field atlas on
the GPU. The runner captures their raw RGBA output, requires byte equality, and
then composes the two sequential captures with measured GPU timing.

| Pressure | Scan work per plan | Selected | Median | P95 | vs baseline |
|---|---:|---|---:|---:|---:|
| Low | 262K × 1 | retained baseline | 0.7266 ms | 0.7341 ms | 1.0000x |
| Medium | 1M × 2 | group 512, EPT 1 | 0.8521 ms | 0.8550 ms | 1.0180x |
| High | 4M × 4 | retained baseline | 1.4349 ms | 1.4408 ms | 1.0000x |
| Extreme | 16M × 8 | group 256, EPT 2 | 4.3552 ms | 4.3866 ms | 1.0341x |

All 48 candidates passed the GPU-output oracle and stability budget. Extreme
pressure produced the largest deployable end-to-end improvement; low and high
correctly retained the baseline under the 1.01x guard. See the
[full visual evidence and all four GIFs](docs/results/R9700_VISUAL_SHOWCASE_2026-09-02.md).

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
- `exclusive-scan-u32-v1`: compares hierarchical Blelloch, wave-hybrid, and
  persistent decoupled-look-back single-pass backends.
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
    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/transpose.json
    dotnet run --project src/HlslPerf.Showcase -c Release --no-build
    dotnet run --project src/HlslPerf.Showcase -c Release --no-build -- --stress-grid --budget-ms 8.333333

RGA is optional and never redistributed. If its CLI is installed or unpacked,
attach live-driver evidence with:

    dotnet run --project src/HlslPerf.Cli -c Release --no-build -- tune manifests/scan.json --rga C:/tools/rga/rga.exe --rga-target gfx1201

Use `--rga off` to disable auto-discovery. Without RGA, correctness, timing,
selection, and profile emission still work.

Outputs go to `.hlslperf/runs/<timestamp>/`. GPU timing is never cached; only
DXIL compilation is cached by source/compiler/options/entry-point/define
identity. The profile compatibility key includes device, driver, backend,
shader model, compiler, manifest hash, and kernel hash.

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
and [clean-room provenance](docs/PROVENANCE.md).
