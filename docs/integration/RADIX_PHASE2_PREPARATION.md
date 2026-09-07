# Conditional Radix phase-two preparation

Status: conditional, not dispatched. No Radix GPU work has run in this round.
The parent must first confirm completion of every project's first phase. This
document prepares a concrete control choice and analysis design; a separately
timestamped, hashed execution ledger must freeze the actual source, binaries,
manifests and process order after that dispatch and before any Radix sample.

The requested matrix is 1 Mi / 4 Mi records, uint keys / stable key+index payload,
uniform full uint (`keyPattern=1`, `keyDomain=1`, 32 bits) / duplicate-heavy
(`keyPattern=2`, seven key values), one / three resident input slots: 16 cells,
five independent processes each. No dynamic dispatch or parameter sweep.

Prepared controls use existing source implementations, not a claim of a new global
optimum: keys use 1-bit, group 256, four items/thread, scalar loads, wave32 backend 2;
pairs use 1-bit, group 128, four items/thread, scalar loads, wave64 backend 2.
These are the earlier declared large warm-cell binary controls; the new 1/4 Mi,
distribution and residency cells still need their own stable-baseline evidence.
The 8-bit candidate remains group 128, two items/thread, scalar loads, wave32
backend 2. The only 4-bit controls are the 4 Mi uniform three-slot keys cell and
the matching pairs cell, using that same wide configuration. Include complete
digit/scan/scatter/split plans and full key/payload poison-oracle verification.

Before candidate performance can be accepted, establish the fixed 1-bit control's
stability in the declared cell. The exact preflight arrangement and its budget
must be frozen in the phase-two ledger; it must not silently add an unbounded
baseline search or use confirmation to replace the control. An unstable control
produces an explicit inconclusive cell, preserving fixed planned repetitions.
This unresolved execution detail is deliberately not represented as already frozen.

Retain eight paired calibration and eight independent confirmation blocks, CV
0.05, drift 0.15, p95 guard and 1.01 lower-bound benefit margin. Use fresh ranges
starting at 100,000,000 with disjoint cell/process/phase offsets, a predeclared
round-interleaved SHA-ordered schedule and a 512 MiB per-arm allocation cap.
The final manifest generator must validate actual four-Mi pair allocation before
native sampling; exceeding the cap is unsupported, not a reason to silently
change a configuration. Keep raw batch duration and repeat count.

Aggregate independent process log ratios equally using df=4 Student-t intervals;
recommend only an identical independently confirmed candidate passing all five
individual gates and the cross-process 1.01 margin. Retain unsuccessful selections,
noisy runs, all seeds and original compiler/output hashes. Calibration alone is
exploratory. No default promotion, original-checkout merge or push is implied.
