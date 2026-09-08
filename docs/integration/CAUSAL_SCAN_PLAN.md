# Fixed Scan IO-granularity experiment

Baseline: published main 67156695bb0eed034078638321212e6df6f98d0a. Its ten focused declaration paths match both checkout and Git blob SHA256 before edits. Historical declarations/evidence remain unchanged. Radix is not sampled.

Hypothesis: the existing vector4 branch reduces load/store instruction granularity cost while retaining semantic local prefix work. The sole candidate changes HLSLPERF_VECTOR_WIDTH from 1 to 4; group256, wave32, 16 elements/lane, 4096-element partitions, 2048 logical partitions, 256 persistent workers, input order, uint32 modulo semantics, lookback/reset, and complete output are fixed. Neighboring lanes still start 64 bytes apart. This is not a lane-transpose experiment or a bandwidth measurement. No extra logical bytes or LDS are requested. Register allocation, chunk lowering, and scheduling may change; actual device ISA/occupancy/DRAM counters are unavailable unless measured explicitly.

First run index0: 17 boundary sizes including 8 Mi and four patterns, plus all three full-size arms, with two poisoned full-output oracle checks; no performance timing. Export actual compiled DXIL and disassembly using the same flags as the executor and require matching binary hashes. Check scalar/vector load/store masks and unchanged wave/local/atomic/barrier structure before interpreting the mechanism.

Development diagnostics: exactly three independent processes, 8 Mi uniform full uint32, one slot. Baseline scalar, sole vector4 candidate, official default RTS. Seed1909087+processIndex*7907, eight six-operation warmup batches/arm, six blocks covering all six permutations with reversed second half, twelve eighteen-operation full batches/arm. No additional pass markers, software counters, or hardware-state changes. Do not pool with formal evidence. Native exceptions/device loss stop the epoch; preserve every failure. No tuning or retries for favorable timing.

After mechanism and correctness validation, decide whether this one candidate supports a single frozen five-process confirmation; if not, report rejection without another candidate. Formal acceptance retains paired process-log 95% CI, CV<=5%, drift<=15%, every-process p95 ratio>=1.01 and lower95>=1.01. No default promotion. All builds/tests/GPU work use the shared mutex without nesting.

## Freeze decision after diagnostics

The candidate passed 68 boundary/pattern cases plus all three arms at 8 Mi (142 complete poisoned output checks). Actual exported DXIL hashes match executor hashes. Full-block IO has four mask15 loads/stores versus sixteen mask1 calls in the baseline; tail branches remain in the static module. Both contain one wave-prefix and one wave-active intrinsic, ten static barrier calls, three atomic-binop calls and one atomic-CAS call. These static sites do not count runtime retries or hardware instructions. The reset DXIL is identical. No kernel source changed.

Three development processes completed once with all output checks passing. Candidate mean times are 0.072735/0.074046/0.092589 ms versus baseline 0.173018/0.176133/0.217519 ms. Variability is substantial (candidate CV16.48%/20.52%/65.20%; RTS also varies). These are descriptive diagnostics only. The verified mechanism and consistent lower point estimates justify exactly one frozen five-process confirmation, with independent new seeds and no altered warmup, thresholds or additional candidate. A stability failure will remain inconclusive, without another sampling attempt.

One build compiled with a readonly-argument warning and failed two new tests because nested shader arrays used reference equality. The test now compares settings/array contents, and the disassembly argument uses `in`. This did not change the candidate kernel. The next build passed108 Core and3 Python tests with no warnings; all failures are retained.

## Retained pre-measurement failures

The new byte audit rejected scan.hlsl because changing its attributes did not restage pre-existing Git index bytes. An explicit `git add --renormalize` preserved the measured checkout bytes under -text/-eol. No HLSL text/semantics changed. The rejection is recorded before declaration generation.

The first frozen formal process exited with usage code1 before creating a process record or D3D12 device: the wrapper had accidentally appended a diagnostic process-index argument to formal mode. Fix the dispatch condition and assert the exact argument count before launch. Retain the v1 declaration, failed receipt/logs and build. A v2 declaration changes only source hashes for the wrapper, with exactly the same arms, seeds, schedule, thresholds and workloads. Use a new build/output epoch; no performance observations existed to discard or select.
