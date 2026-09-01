# HlslKernelPipeline

A clean-room, engine-neutral HLSL kernel tuning pipeline for Windows/D3D12.
It compiles a bounded parameter space, rejects incorrect variants, measures
steady-state GPU time with timestamp queries, and emits a device-specific
selection profile plus a self-contained comparison report.

This repository is independent personal work. It contains no employer, client,
or unrelated project source, assets, configuration, or history.

## Current v0.1 evidence

![R9700 candidate comparison](docs/results/r9700-final.gif)

On an AMD Radeon AI PRO R9700, three calibrated runs selected the same variant.
The guarded final run measured 1.022x median speedup over the declared baseline
with no p95 regression. This is a synthetic pipeline validation result, not a
parameter recommendation for unrelated kernels.

## What v0.1 proves

    manifest + HLSL
          |
          v
    candidate expansion --> DXC --> D3D12 dispatch --> correctness gate
                                                    |
                                                    v
                                    warm-up + timestamp batches
                                                    |
                                                    v
                             JSON profile + CSV + standalone HTML

The first tuning dimensions are thread-group size and elements per thread.
They are expressed as manifest axes rather than hardcoded presets, so later
kernels can tune tiling, unroll count, wave strategy, or data layout using the
same runner.

## Deliberate boundaries

- Reuse DXC for compilation and Vortice for D3D12 interop.
- Start with deterministic exhaustive enumeration for small spaces. A future
  adapter can delegate large searches to Kernel Tuner/KTT-style optimizers.
- Treat PIX/RGA/vendor counters as optional evidence adapters, not dependencies.
- Keep HlslPerf.Core free of Unity types. A Unity package will consume the
  generated profile after the standalone runner is trustworthy.
- Never select a faster candidate that fails byte-for-byte correctness.
- Calibrate each timestamp batch to a minimum GPU duration, rather than trusting
  a fixed dispatch count whose signal may be smaller than normal clock noise.
- Cache DXIL by compiler/options/source/define identity; never cache GPU timing.

## Quick start

Requirements: Windows 10/11, a D3D12-capable GPU, and .NET 10 SDK.

    dotnet run --project src/HlslPerf.Cli -- tune manifests/uint-mix.json

Use manifests/uint-mix-boundary.json for a slower 30-candidate boundary sweep
that includes deliberately low-occupancy configurations.

The HTML animates its comparison bars and supports deterministic capture
frames. GIF export is optional and kept outside the benchmark:

    powershell -File tools/export-report-gif.ps1 \
      .hlslperf/runs/<timestamp>/report.html

Outputs are written under .hlslperf/runs/<timestamp>/. The selected profile is
keyed by adapter identity, driver version when available, backend, shader model,
compiler version, kernel hash, and manifest hash. A mismatch invalidates the
profile instead of silently reusing stale results.

See [measurement methodology](docs/METHODOLOGY.md), [clean-room
provenance](docs/PROVENANCE.md), and the first [R9700 evidence
report](docs/results/R9700_2026-09-01.md).

## Roadmap after the vertical slice

1. Unity adapter that resolves a profile and applies keywords/constants.
2. RGA/PIX evidence import for register pressure, occupancy, and wave analysis.
3. Multi-kernel suites and regression budgets suitable for CI hardware labs.
4. Optional smarter search adapter only when exhaustive search stops scaling.
