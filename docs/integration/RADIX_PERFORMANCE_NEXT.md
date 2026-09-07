# Radix phase-two preflight and conditional comparison declaration

The parent dispatched phase two after all first phases completed. This declaration
resolves the preflight detail left open in `RADIX_PHASE2_PREPARATION.md`. Preserve
the completed Scan source, binaries, raw evidence, reports and source tip
`f787f741becf7a792c5a39ea86c58361c3ca86cb`. Build Radix into a separate artifacts
directory; never overwrite Scan's frozen runtime. No original-branch merge, push,
default promotion, dynamic rerun or parameter sweep is authorized.

## Planned cells and immutable controls

Sixteen cells: 1 Mi / 4 Mi records, uint keys / stable key+original-index payload,
uniform full uint / duplicate-heavy seven-value keys, one / three genuinely
different resident input slots. Mi is 1,048,576. Use all 32 key bits.

Fixed 1-bit control: keys use group 256, four items/thread, scalar loads, wave32
scan backend 2; pairs use group 128, four items/thread, scalar loads, wave64 backend
2. These historical control choices are frozen before new samples, not claimed
as a fresh exhaustive optimum. The 8-bit candidate is group 128, two items/thread,
scalar loads, wave32 backend 2. Include a 4-bit candidate with that wide configuration
only in the 4 Mi uniform three-slot keys cell and the matching pairs cell. If its
cell fails preflight, that 4-bit control is not applicable; do not replace it with
another cell. No candidate parameters change after looking at either phase.

## Bounded baseline preflight and entry rule

Run five independent native processes for every planned cell, 80 preflight
processes total. Each process contains only its one fixed binary control. The
unchanged paired-v2 protocol executes eight baseline self-control calibration
blocks and eight fresh-seed self-control confirmation blocks: 64 observations.
Each block has four randomized ABBA/BAAB positions, retaining separate A/B resident
rings. Every declared output in every ring slot must pass both poison values and
the CPU oracle, including both sorted keys and stable payloads.

Individual preflight eligibility requires: compilation and every correctness check
pass, the sole calibration comparison passes, independent confirmation and the
existing final deployment gate pass, and the candidate calibration summary is
stable. Retain CV <= 0.05 and drift <= 0.15 within/between the existing phases.
Baseline self-controls have no artificial 1.01 benefit requirement. Additionally,
across the five process confirmation baseline-arm medians, require sample CV <= 0.05
and max/min - 1 <= 0.15. These two cross-process stability limits are declared now,
before data collection. Report the equally weighted process self-ratio interval
descriptively, not as a performance improvement.

A cell enters comparison only if all five planned processes satisfy those rules,
their identities are comparable, and independent raw-data audit succeeds. An
unsupported/memory-failed process makes the cell unsupported; any other stability
failure makes its comparison inconclusive/not applicable. Do not search for a
replacement control or repeat a process until it passes. Finish all 80 preflight
processes, audit them, then write a separate immutable entry ledger binding the
declaration and every preflight report hash before starting any wide comparison.

## Conditional comparisons and statistics

Freeze all 80 conditional comparison manifests and their full order in the initial
declaration. Execute exactly five new independent processes per eligible cell;
unqualified cells retain five explicitly unexecuted conditional entries and their
reasons. The two designated 4-bit cells have three candidates; other comparisons
have binary and 8-bit only. Calibration selects only among these fixed candidates;
confirmation locks that selection and uses fresh input seeds/timing. Never select
a different candidate from confirmation or transfer samples from preflight.

Retain eight calibration blocks per challenger and eight independent confirmation
blocks, CV 0.05, baseline drift 0.15, candidate p95 no worse than paired baseline,
and lower paired 95% speedup bound >= 1.01. Four warmup plans repeat for at least
25 ms. A measured batch is exactly 18 complete plans (divisible by three slots),
minimum total batch 0.25 ms; maximum repetitions also 18. Include every digit,
histogram/flag, hierarchical scan, scatter, pair-output split and transition.
Upload, PSO creation, post-timing poison/readback and host oracle are excluded.
These are amortized complete-plan GPU costs, not application-frame latency.

Process preflight seed = 100,000,000 + cellIndex*1,000,000 + (round-1)*10,000;
comparison uses 300,000,000 instead. Confirmation starts 5,000 later. Order seed
is the calibration seed + 73019. Within each round visit all cells, sorted by
SHA-256 of `radix-next/709127/<preflight-or-comparison>/<round>/<cell-name>`.
The comparison schedule is filtered by the entry ledger without reordering.

Aggregate five process mean log ratios equally using Student-t(df=4,
critical=2.7764451051977987), pointwise two-sided 95% intervals. Recommend only an
identical non-baseline selection passing all five original individual deployment
gates and cross-process lower bound >= 1.01. Mixed selections or one failed gate
remain inconclusive even if the aggregate ratio looks favorable. P95/P99 are
linear-interpolated within each process and summarized with equal process weight;
they do not establish a hard frame bound. Keep calibration exploratory.

Each arm is capped at 512 MiB, two simultaneous arms <= 1 GiB. Record actual
committed/default-heap allocation, logical bytes, scratch bytes, digit and dispatch
counts for each supported candidate. Exact allocation is checked by the native
scenario constructor before timing; failures are retained with the original
size/slots/configuration, never silently reduced. Normal WDDM residency and real
rotation do not imply pinned residency or guaranteed cache-cold data.

All heavy builds and GPU work hold `Local\CodexR9700VNextUnityGpu` until the native
process exits. Freeze source/compiler/runtime/device/driver and actual input
identities. Leave user applications, caches, drivers and power settings alone.
Queue time is not GPU timing. All failures/noise/unsupported combinations remain
in the final ledger. Maximum scheduled work is 80 preflight + 80 conditional native
processes, plus offline analysis; no unbounded retry or extra tuning search.
