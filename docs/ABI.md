# Kernel ABI v1

`hlslperf.raw-buffer.v1` is an engine-neutral execution ABI, not a Unity shader
API and not a manifest expression language.

## HLSL bindings

| Root slot | HLSL binding | Meaning |
|---:|---|---|
| 0 | `ByteAddressBuffer` at `t0` | read-only input 0 |
| 1 | `ByteAddressBuffer` at `t1` | optional read-only input 1 |
| 2 | `RWByteAddressBuffer` at `u0` | writable output 0 |
| 3 | `RWByteAddressBuffer` at `u1` | optional writable output 1 |
| 4 | eight uint root constants at `b0` | pass-local scalar parameters |

The canonical root signature is:

    SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)

Unused slots remain valid but must not be accessed. ABI v1 deliberately uses raw
buffers so resource layout is explicit and independent of an engine's structured
buffer reflection rules.

## Execution-plan contract

An `IKernelWorkload` builds a candidate-specific `KernelExecutionPlan` containing:

- named buffers with exact byte lengths and optional deterministic initial data;
- one or more passes with entry point, 1D/2D/3D dispatch dimensions, four resource
  names, and up to eight constants;
- a logical item count for throughput reporting;
- one verified output buffer and its CPU-oracle SHA-256.

The backend compiles every distinct entry point with the candidate's sorted
defines. It tracks resource state across passes, inserts the required SRV/UAV
transitions, and times the complete plan. Upload, compilation, readback, and CPU
hashing are outside the timestamp interval.

The same resource cannot be bound as an SRV and UAV in one pass. An algorithm
that needs in-place work can split it into passes or use a read/write UAV kernel.

## Schema migration

Schema 1.0 manifests are still accepted through the `uint-mix-v1` workload
adapter. Schema 2.0 adds `kernelAbiVersion` and a workload id/parameter object.
The D3D12 executor itself has no workload-specific branches.

ABI extensions should receive a new id rather than silently changing root slots.
