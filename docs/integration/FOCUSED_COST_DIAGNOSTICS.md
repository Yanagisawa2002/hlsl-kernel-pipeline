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

## One selected candidate

Both retained diagnostic versions place about 69% of internal Radix time in
scatter. The sole candidate replaces the quadratic predecessor equality loop
with valid wave bit planes and population counts. Group size 128, two records per
thread and eight-bit digits stay fixed. Its scatter explicitly requests wave32
(the previous scatter entry point did not apply the existing wave32 macro).
It adds 288 bytes of shared bit planes and one group barrier; this is a single
fixed rank implementation, not a wave-size sweep. It preserves key/payload loads,
stable original order, histogram and prefix work, all 29 passes and conversion.
The default macro is zero; only the focused arm enables it. Native correctness
covers 18 boundary sizes including 1 Mi, five patterns and both poisoned complete
key/payload outputs. No performance pilot selects among candidates.

Scan has no candidate: its observed algorithm cost, persistent lookback polling,
scalar access pattern and intra-block work do not isolate a safe fixed repair.
The confirmation repeats its unchanged two-arm comparison for contemporaneous
context. `focused_cost_protocol.py generate` freezes exactly these two cells,
three Radix arms and two Scan arms, five independent processes/cell, 12 balanced
blocks, 18 operations/batch, and the previous 95% CI/CV/drift/p95 contract. No
pass markers or diagnostic counters enter confirmation. All outcomes stay visible.

## Reproduction and evidence identities

Measurement commit: `3874eb0908539db5bf65777eacd54b2ab8bd8549`.
Frozen declaration SHA256:
`23dd2cce9cb6c1c0e2c05e799e1f7b5cf14aea72fa5f5802a96485541a779d25`.
The later result/documentation/audit commit does not change measurement code.
Use a clean checkout of the measurement commit for exact source reproduction,
new output directories, and the included shared-lock helper. Do not wrap the
build helper in another lock. A re-created runtime has its own binary identity;
use the delivered runtime archive and lock when checking original binaries.

```powershell
./tools/Build-UnifiedBenchmark.ps1 -OutputDirectory .hlslperf/focused-reproduction/build
$b = Get-Content -Raw .hlslperf/focused-reproduction/build/build.json | ConvertFrom-Json
./tools/Invoke-UnifiedExperiment.ps1 -Mode focused-correctness -Runtime $b.runtime `
  -OutputDirectory .hlslperf/focused-reproduction/correctness `
  -SerializedValidationRunner $b.serializedValidationRunner
$d = Get-Content -Raw docs/integration/focused-cost-declaration.json | ConvertFrom-Json
foreach ($item in $d.processOrder) {
  ./tools/Invoke-UnifiedExperiment.ps1 -Mode focused-formal -Runtime $b.runtime `
    -OutputDirectory ('.hlslperf/focused-reproduction/formal/'+$item.cell+'-p'+$item.process) `
    -Declaration docs/integration/focused-cost-declaration.json -Cell $item.cell `
    -ProcessIndex $item.process -ExpectedSourceSha $b.sourceSha -BinaryLock $b.binaryLock `
    -SerializedValidationRunner $b.serializedValidationRunner
}
python tools/focused_cost_protocol.py analyze docs/integration/focused-cost-declaration.json `
  .hlslperf/focused-reproduction/formal .hlslperf/focused-reproduction/audit
```

The delivered receipts retain the actual absolute commands and process IDs.
Original diagnostics use source `819507f6ba0cb3b9b6f987d3b242f333f28e7c3e`
(one operation) and `7d5c479d3a36f29b8f3f45af8f61d3e491a113df` (18 operations).
Each runs `focused-diagnostic <repo> <new-output> <index>` for indices 1..3
through `Invoke-UnifiedExperiment.ps1`. Candidate correctness uses source
`4e324e1bd256d2d9f8c10fc7f87a7bb0a7c2875a`, mode `focused-correctness`.
The initial candidate build `e16aef8` compiled but its array-reference test failed;
the following commit fixes only that assertion. No native candidate run was
performed from the failed build.

The final checkout adds read-only audits:

```powershell
python tools/summarize_focused_diagnostics.py .hlslperf/focused-costs diagnostic-audit-new.json
python tools/audit_focused_provenance.py . .hlslperf/focused-costs provenance-audit-new.json
```

The provenance audit verifies the recorded absolute files on the collecting
machine. For an offline/moved archive, use its per-file SHA256 manifest and the
frozen source checkout to resolve those paths; do not silently edit original
receipts. Formal and diagnostic sources/binaries are separate, never pooled.
The complete fixed candidate includes the new explicit WaveSize32 and assumes
R9700 lane grouping; no cross-vendor portability or pure rank-only attribution
is established. See `FOCUSED_COST_RESULTS.md` for all failed stability gates.
