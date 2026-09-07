# Scan process-level performance declaration

This experiment starts at `18c2e19500063b1749a9bb0315e22d3070a47ade`.
It is separate from the previous vNext integration. No default promotion, push,
dynamic-dispatch rerun or exhaustive parameter search is authorized here.

## Fixed first-phase design

Eight cells: 4 / 8 / 12 / 16 Mi uint elements (Mi = 1,048,576), each with one or
three simultaneously retained, genuinely different input slots. Five new native
processes per cell, 40 total. Round 1 visits all cells before round 2; each round's
cell order is sorted by SHA-256 of `scan-next/613779/<round>/<cell-name>`.
The complete process order and every manifest are saved before any sampling.

All cells retain exactly these existing configurations: group 256, four elements
per thread, scalar loads, addition scan; backend 1 LDS baseline, backend 2 wave32,
backend 3 persistent single-pass wave32 with scale 4 and 256 persistent groups.
No thread configuration is chosen from the new results. Within a process the
existing tuner selects among this fixed set using calibration only; its selected
candidate is locked before independent confirmation. A baseline self-control
confirmation cannot be relabelled as a single-pass confirmation.

Use the existing `gpu-paired-abba-independent-confirmation-v2` protocol unchanged:
eight randomized ABBA/BAAB blocks per candidate in calibration and eight blocks for
the locked confirmation, CV <= 0.05, baseline drift <= 0.15, candidate p95 no worse
than paired baseline, lower 95% speedup bound >= 1.01. Four warmup plans repeat for
at least 25 ms. Each timing batch executes 36 complete plans, cycling equally
through resident slots. Minimum batch 0.25 ms, maximum and initial repetitions both
36. These are complete-plan amortized GPU costs, not isolated dispatch or real
application frame latency. Upload, PSO creation, poison/readback and host oracles
are excluded; all plan resets, scans and transitions are included.

Calibration seed starts at 60,000,000 + cellIndex*100,000 + (round-1)*10,000;
confirmation starts 5,000 later; block slots use the existing consecutive seed
rule. Seeds are unique across process/cell/phase. Order seed is calibration seed
+ 73019. Fresh correctness-only seeds start at 90,000,000. Each arm is capped at
512 MiB (two arms <= 1 GiB); actual logical/default-heap bytes and DXGI budgets
are retained. Rotation is not claimed to be cache-cold or pinned residency.

Separate native correctness cases use sizes 4 Mi+3, 8 Mi+127, 12 Mi+1 and
16 Mi+4095, both slot counts and all three fixed candidates: 24 scenarios. Every
resident output is verified with both poison patterns and its independent CPU
oracle. This correctness harness does not produce performance evidence.

## Process-level analysis and stopping rules

The independent process is the top-level replication unit. Compute each process's
confirmation mean log speedup from its eight paired blocks, then equally weight
the five process means. Use Student-t(df=4, 2.7764451051977987) for the two-sided
95% interval; never treat 80 arm samples as 80 independent processes. Five must
confirm the identical non-baseline candidate and pass all existing individual
deployment gates, and the cross-process lower bound must reach 1.01, before an
exact configuration is recommended. Mixed selections, noisy/failed processes or
an interval crossing the margin are inconclusive. Retained baseline profiles do
not count as gains. Report every process and cell regardless of acceptance.

P95 and P99 use linear interpolation on each process's raw amortized-plan samples;
report the five process quantiles and their equal-process arithmetic mean with
a df=4 t interval (negative latency lower endpoints clamped to zero for display).
They are descriptive estimates from short batches, especially P99, not hard
single-frame bounds. Raw batch duration is sample*36 and is retained alongside
the original repeat count. Calibration per fixed candidate is reported separately
as exploratory evidence; it is never substituted for independent confirmation.

Optional refinement is permitted once only: require an adjacent pair in the same
slot scenario with five stable confirmations of the same non-baseline candidate,
the lower size's process upper bound <= 1.01 and the upper size passing the gain
rule. Declare at most four distinct midpoint sizes in a new ledger before sampling,
with fresh seeds and the same five-process protocol. If this stringent trigger is
absent, do not refine or infer a global crossover threshold. No retry-until-pass;
all 40 processes must finish or be explicitly recorded failed. Reproducible code
faults require a new source/output identity and retain the failed attempt.

The shared `Local\CodexR9700VNextUnityGpu` mutex covers every build, native smoke
and measurement process until exit. Record PID/start/exit/source/compiler/device,
external application snapshots and unavailable thermal/clock controls. Never stop
user processes, clear global caches or alter drivers/power/permissions.

## Conditional Radix preparation only

Second-phase GPU execution remains blocked on the parent dispatch after all first
phases finish. Planned axes: 1 Mi / 4 Mi records, keys / pairs, uniform /
duplicate-heavy, one / three slots; five independent processes, complete plans,
stable 1-bit control before comparing 8-bit. Preserve one declared large keys and
one large pairs 4-bit control without a parameter sweep. Freeze exact controls,
seeds, process order, binary and memory budget in a separate declaration before
any second-phase sample. No first-phase data is Radix confirmation evidence.
