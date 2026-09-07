# Resident working-set scenarios

`D3D12Tuner.PrepareScenario(manifest, manifestPath, workload, candidate, scenario)`
returns an `IDisposable ScenarioSession`. All default-heap resource sets remain
allocated together. `MeasureBatch(runCount, startSlot = 0)` returns **total** GPU
milliseconds, alternating resource slots on every complete-plan execution. The
caller divides by runCount and records startSlot/runCount. Warmup must be explicit.
Uploads, PSO compilation, CPU oracles, poison and readback are outside timestamps;
all plan passes, reset passes and resource transitions are inside.

`WorkloadScenario(Id, InputSeeds, ElementCount = null, MaximumAllocationBytes =
512 MiB)` requires 1..32 distinct positive seeds. One slot represents the existing
cache-warm repeated-buffer case. Multiple slots rotate buffers and deterministic
inputs. **Rotation does not guarantee cache-cold data.** WDDM can evict allocations;
ordinary committed resources are not pinned. Temperature, clocks and unrelated
applications are uncontrolled unless separately captured by the coordinator.

`Apply` clones both correctness.seed and workload.parameters.seed, preserving
other manifest fields. An optional elementCount override changes actual workload
size; it rejects workloads lacking that parameter. `Build` rejects workloads
whose initialized-buffer hashes fail to change with the seeds. The CPU workload
provider rebuilds each slot's independent expected output hash.

The session records logical buffers, D3D12 allocation-info sizes (including one
dummy allocation per slot), initial upload bytes and maximum primary readback
bytes (peak single-buffer readback across every declared output, allocated serially).
Upload totals are traffic/storage descriptions, not GPU bandwidth. Caps
apply to committed DEFAULT allocations; host oracle memory and transient
upload/readback allocations are additional. Allocation fails before resource
creation if it exceeds the declared cap or 75% of the current available DXGI
local-memory budget. Snapshot budget/usage is process-wide, not proof of per-buffer
residency. Missing budget data remains null with its error.

## Paired protocol integration

For each phase/block, build the same seed ring for candidate and baseline and
hold both sessions alive throughout its ABBA/BAAB sequence. Use the same runCount
and startSlot for every arm, preferably runCount a multiple of ring size. Both
arms must pass `VerifyAll()` before their measurements are eligible. Warm each
arm explicitly, then sample. Recheck every slot after timing; retain failures.
Dispose both sessions before the next block. Calibration and confirmation must
use disjoint complete seed rings; lock candidate selection before confirmation.

`VerifyAll` uses the dynamic executor's common `VerifyPlanOutputs` helper: every
declared output is poisoned twice with distinct patterns, then executed and
read back against its own oracle. `Correctness.Outputs` retains each resource
and attempt. The optional `(slot, primaryBytes)` callback preserves existing
output consumers. Slot evidence includes all expected output hashes.

Record session Evidence with each pair. IdentitySha256 binds scenario/seeds,
manifest, candidate, device/driver, source graph, workload assembly, input hashes
and actual DXIL hashes. Compare arms by the declared workload/seed/count/cache
policy and shared timing scope, not equality of candidate-specific session hashes.
Reject baseline/candidate input or oracle mismatches and changes to the selected
protocol. Never pool historical repeated-buffer measurements with these records.

The measurement worker owns manifest declaration and paired scheduling. The
scenario API itself produces evidence, not a deployment selection or completed
performance gate. The final integration task runs the bounded formal comparison.
