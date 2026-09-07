# R9700 working-set and compiler resource evidence

The final bounded hardware smoke passed all five cells, with 52 successful
poison/re-execution output comparisons. All four RGA invocations collected
statistics and raw artifacts. This validates the scenario and evidence paths;
it does not establish a performance winner or a deployable profile.

Final measured source commit: `73a03e1883308bf0fd50f40f5c89f3a814efffdd`.
The build started from a clean tree and the source files remained unchanged
during the run. Release build: zero warnings/errors. Core tests: 45 passed,
zero failed/skipped. The final hardware receipt covers 2026-09-07
13:20:27–13:20:30 UTC+08:00; shared-lock wait time is excluded from this interval.

Device: AMD Radeon AI PRO R9700, driver `32.0.31041.1004`, Windows
`10.0.26200`, RGA target `gfx1201`. Native execution loaded DXC
`1.9.2602.17`; RGA used its explicitly identified DXC `1.8.2502.8`.
Both native compiler modules and actual execution DXIL have SHA-256 identities
in the receipts. RGA recompiles the same identified source/defines; its output
is not asserted byte-identical to the measured shader binary.

## Declared scenarios and memory

All candidates here use backend 3, group size 256, four elements per thread,
single-pass scale 4, persistent-group limit 256. Scan uses cs_6_0 and compaction
uses cs_6_6. Seeds are 104729, 130363, 155921 (the warm cell uses only 104729).
Each timed batch executes twelve complete plans, starting at slot zero; there
are three diagnostic batches and one twelve-plan warmup. Input upload and host
oracle/readback are excluded, while reset and transitions are included.

| Cell | Elements | Slots | Logical bytes | DEFAULT allocation bytes | Correctness |
|---|---:|---:|---:|---:|---|
| Scan, repeated warm buffers | 262,147 | 1 | 2,097,964 | 2,359,296 | Pass |
| Scan, rotating buffers | 262,147 | 3 | 6,293,892 | 7,077,888 | Pass |
| Scan, larger rotation | 4,194,307 | 3 | 100,700,292 | 101,449,728 | Pass |
| Fused compaction, rotation | 262,147 | 3 | 3,540,760 | 4,194,304 | Pass |
| Fused compaction, larger rotation | 4,194,307 | 3 | 56,660,084 | 57,409,536 | Pass |

Buffers are held together throughout each session. Each rotation uses different
actual initialized-buffer hashes and independent CPU expected hashes, including
partial-block boundaries. The common dynamic executor verifies every declared
output with both 0xa5 and 0x5a poison patterns before and after timing. These five
workloads declare a single primary output; separate v2 multi-output workloads
remain covered by the dynamic worker and integrated validation.

The 512 MiB per-session cap checks actual D3D12 default-heap allocation sizes.
Initial upload bytes, maximum single-output readback size, and DXGI process
budget/usage snapshots are retained separately. Host oracles and transient
upload/readback allocations are additional. Ordinary WDDM committed allocation
does not prove continuous physical residency. **Rotation is not guaranteed
cache-cold**, and the three smoke timing samples must not be used to infer a
cache capacity, bandwidth, speedup, or steady thermal state.

## RGA static metrics

| Entry | VGPR used | SGPR used | LDS bytes | Scratch bytes | Maximum live VGPR | Allocated VGPR |
|---|---:|---:|---:|---:|---:|---:|
| ResetSinglePassState (scan) | 3 | 14 | 0 | 0 | 2 | 24 |
| SinglePassScan | 45 | 26 | 1,056 | 0 | 43 | 48 |
| ResetSinglePassState (compaction) | 3 | 14 | 0 | 0 | 2 | 24 |
| FusedCompactSinglePass | 44 | 58 | 1,056 | 0 | 42 | 48 |

Spill counts, occupancy and measured bandwidth are unavailable/null. A reported
zero scratch allocation is not a measured spill count. All table values are
static compiler/liveness evidence, not runtime occupancy or bandwidth.

## Retained data and remaining gates

The complete receipts are under
[data/hlsl-vnext-evidence-2026-09-07](data/hlsl-vnext-evidence-2026-09-07).
`attempt-02` is the final clean-build result; `attempt-01` is retained as an
earlier v1-only verification implementation with dirty development-source
provenance. It is not relabelled as final multi-output validation.
Every attempt has `artifacts.json` with relative artifact paths and SHA-256
hashes. Original absolute capture paths inside raw receipts are preserved.
RGA receipts include process output, exact arguments, entrypoint/model/defines,
source snapshots, tool/compiler hashes, statistics, ISA, liveness and pipeline
binaries. Hardware metadata includes actual runtime assembly/native-module
hashes, driver identity and a process snapshot.

Remaining project gates are explicit:

- The integrator must run the selected candidate set using paired calibration
  and independent confirmation on fresh, disjoint seed rings. No smoke timing
  here replaces that experiment or validates the final merged binary.
- Temperature, clocks, per-resource residency/eviction and external application
  GPU interference were not measured or controlled. The shared mutex serializes
  only cooperating validation. A process snapshot is not GPU attribution.
- Runtime occupancy/bandwidth and explicit spill-count metrics are unavailable
  from this provider. Keep them null; additional capture requires another real
  provider if those metrics become an acceptance requirement.

Reproduction uses `tools/Invoke-BoundedHardwareEvidence.ps1` and the predeclared
`manifests/evidence/bounded-r9700.json`, with the existing RGA install and shared
validation lock. See [the adapter guide](../RGA.md) and
[scenario contract](../WORKLOAD_SCENARIOS.md).
