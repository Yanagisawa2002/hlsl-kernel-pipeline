# Fixed cost diagnostics

This work starts at published main `9afb7f1f729a1931051a7a817f2d6b2b20680db9`
and preserves the previous experiment. It covers only Radix 1 Mi uint32 pairs,
uniform, one resident slot (internal eight-bit versus AMD), and Scan 8 Mi uint32
exclusive modulo addition, one slot (internal single-pass versus RTS).

Before any new GPU work, prior process records and source are inspected. The
internal radix scatter explicitly scans all earlier records to compute each
stable local rank: 256*255/2 comparisons per tile, 4096 tiles per digit and four
digits, or 534,773,760 shared-memory comparisons per full operation. This is a
source-level operation count, not an instruction or hardware memory counter.

The development diagnostic is fixed to three processes, with seed
20260908 + processIndex*977 + (scan ? 31 : 0). Each process runs both cells,
two full-output poison checks before and after, four six-operation warmup batches,
and six ABBA/BAAB blocks alternating with process index. A diagnostic operation
records one timestamp before work and one after each pass, with a single queue
submission and fence completion. It does not fence between passes. Additional
markers may change scheduling/cost, so these timings are never confirmation data.

The first retained diagnostic used one operation per submission. Its timings,
especially Scan's first pass, differed substantially from the prior 18-operation
formal batches. The revised diagnostic therefore uses exactly 18 operations per
submission, with a dedicated query heap, while retaining per-pass markers. Pass
times are averages per operation; the total field is the whole batch. This is a
measurement-granularity correction, not a performance candidate or a parameter
search. Both versions and their failure/limitation evidence are retained.

Scan additionally runs one opt-in instrumentation variant at the same size and
configuration. Three snapshots per process retain every block's status polls,
aggregate hits, prefix hits and not-ready polls. They have independent output
correctness checks and accounting validation. Instrumentation adds stores and
registers and may change contention; its timing is not a formal comparison and
the counters are not DRAM/cache performance counters.

No group size, radix width, scale or slot-count search is authorized. A diagnosis
may justify at most one candidate per cell, frozen before independent five-process
confirmation. No unsupported causal claim or positive gain is required. All
builds, tests and native execution use the existing shared validation mutex.
