# Dynamic raw-buffer execution ABI v2

`hlslperf.raw-buffer.v2` adds execution semantics while preserving the v1 shader
root signature (`t0/t1`, `u0/u1`, eight uint constants). Existing positional plan
and pass constructors and positive-count v1 workloads remain supported. v1 plans
reject v2-only properties instead of silently ignoring them. v2 plans and manifests
allow zero logical work, but allocated buffers still need at least four bytes.

## Public contract

* `KernelPassSpec.DependsOn`: names of earlier passes. The supplied sequence is
  already a topological schedule. Validation rejects missing RAW, WAW and WAR
  dependencies, cycles/forward references and reads without initial data or an
  earlier producer. Transitive dependencies count; chaining the preceding pass is
  valid. UAV read/modify/write initialization remains a shader author's obligation.
* `KernelPassSpec.Indirect`: `KernelIndirectDispatch(CountResource,
  CountByteOffset, ArgumentResource, ArgumentByteOffset, MaximumItemCount,
  ThreadsPerGroup)`. An earlier GPU pass must produce the uint count. Offsets must
  be uint aligned and their 4/12-byte ranges must fit, using wide offset arithmetic.
* `KernelExecutionPlan.AdditionalVerifiedOutputs`: `KernelVerifiedOutput(Resource,
  ExpectedSha256)`. The existing primary `VerifiedResource/ExpectedSha256` remains
  the first output; `GetVerifiedOutputs()` returns both. All hashes cover entire
  buffers, including inactive capacity and padding. Outputs must be produced and
  have distinct names. Providers must supply independent CPU oracles.
* `CorrectnessResult.Outputs` records each resource's actual/expected SHA-256,
  attempt and poison byte. Overall `Passed` requires every check, not just primary.

## Bounded native dispatch

Immediately before each indirect consumer, the executor dispatches its own setup
shader. It reads the GPU count and computes `ceil(min(count, cap) / groupSize)`
without addition overflow. `cap <= 65535 * groupSize`, `1 <= groupSize <= 1024`.
It writes one D3D12 `DISPATCH` argument record `(X, 1, 1)` and deterministically
zeros every other word in the reserved argument buffer. Each argument buffer is
exclusively owned by executor setup; workload UAV bindings cannot modify it.
Sharing a buffer between indirect steps replaces its previous record and padding.

The resource transitions from UAV to `INDIRECT_ARGUMENT`, then `ExecuteIndirect`
uses a dispatch-only command signature with `MaxCommandCount=1`. A zero count
produces `X=0` and no consumer invocations. The count specifies items, not the
number of command records. There is no CPU count readback or CPU decision between
producer and consumer. Output UAV/SRV transitions order producer/consumer memory
accesses and repeated plans. No cross-queue synchronization is implied.

Consumer shaders must use the declared actual thread-group size, clamp their
logical item count to the same cap, and guard the final partial group. They remain
responsible for buffer capacity, index arithmetic and intra-dispatch races; a
bounded dispatch does not make arbitrary shader code memory-safe.

All reset/count-producing passes, bounded-argument setup, resource barriers and
indirect work are inside complete-plan timestamp queries. Resource allocation,
initial host uploads, DXC/PSO creation, poison uploads and correctness readbacks
are outside the GPU timing scope. This is a resident-input compute scope, not
end-to-end host upload latency. The argument buffer clearing cost is included.
The existing Unity profile consumer continues to reject v2 because it has no
dynamic-plan executor; native ABI support does not imply Unity support.

## Verification and executable demo

After timed repetition, every declared output is poisoned with `0xa5`, the whole
plan executes, and every output is hashed. This repeats with `0x5a`. Counts,
argument records, bin totals, compacted values, consumed values and unused capacity
are all independently covered in the demo. Input buffers cannot rely on verified
output initial data surviving poison; outputs must be regenerated each execution.

`DynamicCompactionWorkload` filters/stably compacts actual GPU input, produces a
GPU count, and drives an indirect transform and atomic eight-bin histogram. Its
serial producer is a small correctness example (maximum 65,535 elements), not a
performance candidate or replacement for optimized scan/compaction providers.

The dedicated harness calls `D3D12Tuner.ValidateExecutionPlan` directly and is
independent of tuning protocol defaults. It runs 11 positive v2 inputs including
three changing seeds, zero input/active/capacity, a partial group, capacity clamp,
nonzero offsets, uint-max raw count and exactly 65,535 groups. Negative fixtures
omit a zero-work reset and corrupt only a secondary output. A legacy v1 workload
checks fixed-dispatch compatibility. Every native call repeats resident execution
three times (one for negatives) before both poison attempts.

```powershell
# All builds and GPU execution must be inside the project's shared validation lock.
dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release
dotnet run --project tests/HlslPerf.DynamicSmoke/HlslPerf.DynamicSmoke.csproj -c Release -- . artifacts/dynamic-smoke
```

The JSON retains device/driver, workload and executor source identities, backend
binary identity, per-entry DXIL hashes, complete plan parameters and every check.
Its samples are explicitly correctness-smoke diagnostics, not paired performance
evidence. Post-merge randomized calibration/confirmation with integrated providers
is a separate gate; no speedup or deployment claim follows from this harness.
