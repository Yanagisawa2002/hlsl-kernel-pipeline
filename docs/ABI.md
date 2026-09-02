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
- one fully deterministic verified output buffer and its CPU-oracle SHA-256.

The backend compiles every distinct entry point with the candidate's sorted
defines. It tracks resource state across passes, inserts the required SRV/UAV
transitions, and times the complete plan. Upload, compilation, readback, and CPU
hashing are outside the timestamp interval.

The verified resource is a repeatability contract, not just a final snapshot.
After all timed batches, the backend overwrites every verified byte with `0xA5`,
executes the already-used plan once, then performs readback and hashing. Every
invocation must therefore regenerate the complete resource. Workloads should
size compact outputs to their defined payload rather than relying on untouched
padding bytes.

The same resource cannot be bound as an SRV and UAV in one pass. An algorithm
that needs in-place work can split it into passes or use a read/write UAV kernel.

## Single-pass scan on ABI v1

The bundled backend-3 scan remains inside ABI v1. Its timed execution plan has
an O(1) `ResetSinglePassState` dispatch followed by one `SinglePassScan` data
dispatch. The reset increments an epoch and resets a claim counter; it never
walks the input or per-block state.

`SinglePassScan` binds input at `t0`, final output at `u0`, and a globally
coherent state buffer at `u1`. The state layout is an eight-byte header
`[epoch, nextBlock]` followed by twelve bytes per logical block
`[aggregate, inclusivePrefix, epoch|status]`.

Physical groups atomically claim logical block ids. A claimed predecessor has
therefore already started before a later block can observe its id. Each block
publishes its aggregate, walks backward across ready aggregates, stops at the
nearest completed prefix, publishes its own inclusive prefix, and writes final
exclusive-scan values. This persistent claim order avoids waiting for an
unlaunched group while decoupled look-back avoids a fully serialized prefix
chain.

The scale and persistent-group limit are ordinary integer defines and therefore
participate in candidate identity, DXIL caching, reports, and emitted profiles.
No D3D12 executor branch or Unity-specific binding is required.

## Schema migration

Schema 1.0 manifests are still accepted through the `uint-mix-v1` workload
adapter. Schema 2.0 adds `kernelAbiVersion` and a workload id/parameter object.
Schema 3.0 adds conditional axes and implication constraints without changing
ABI v1. The D3D12 executor itself has no workload-specific branches.

Kernel identity covers the root source and all transitive `.hlsli` dependencies.
Include paths and content hashes are canonicalized, and include cycles are
rejected. The combined hash keys compilation, checkpoints, profiles, and the
Unity consumer.

ABI extensions should receive a new id rather than silently changing root slots.
