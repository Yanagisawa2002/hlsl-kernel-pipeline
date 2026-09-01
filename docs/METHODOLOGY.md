# Measurement methodology

The pipeline separates an observed fast result from a deployable profile. A
candidate is not deployable merely because one number is lower.

## Measurement contract

1. DXC compiles each manifest-defined set of integer defines with O3, strict
   mode, and warnings as errors.
2. A CPU correctness oracle produces the expected output hash. Every GPU
   candidate must match it byte for byte.
3. Each candidate receives at least 75 ms of GPU warm-up in the sample
   manifests.
4. The runner doubles dispatches per timestamp batch until the interval is at
   least 10 ms, capped by the manifest. Fences and readback are outside the
   timestamp interval.
5. Fifteen independent batches produce median, p95, sample standard deviation,
   and coefficient of variation (CV). The sample manifests reject CV above 5%.
6. The observed fastest stable candidate must be at least 1.01x faster than the
   declared baseline and must not regress p95. Otherwise the emitted profile
   keeps the baseline.

GPU timing is never cached. DXIL is cached by source hash, entry point, shader
model, compiler version, compiler option schema, and sorted defines.

## Profile invalidation

The compatibility key includes adapter identity, driver version, backend,
shader model, compiler version, manifest hash, and kernel hash. A change in any
of these inputs invalidates the profile.

## What the v0.1 evidence does not prove

- The bundled uint-mix kernel is synthetic. Its selected parameters must not be
  copied into a production kernel without tuning that production kernel.
- Results currently cover one Windows/D3D12 GPU and compute queue.
- No claim is made about energy, temperature, register pressure, occupancy, or
  instruction count; those require vendor evidence adapters such as RGA/PIX.
- Candidate blocks are not yet interleaved or randomized, and the report does
  not yet calculate confidence intervals.
- The current correctness ABI is one UAV plus two root constants. General
  resource-layout adapters are a post-v0.1 task.

These limitations are explicit so the pipeline remains a measurement tool,
not a benchmark-marketing generator.
