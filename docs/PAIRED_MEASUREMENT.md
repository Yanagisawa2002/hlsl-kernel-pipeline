# Paired measurement and independent confirmation

New manifests default to `gpu-paired-abba-independent-confirmation-v2`. Explicit
`measurementProtocol: "gpu-timestamps-poison-reexecute-v1"` runs the historical
sequential implementation. Historical reports and previous published results do
not acquire paired or independent-confirmation guarantees. The default exporter
does not emit a deployable historical profile. The explicit core
`CreateProfile(report, allowHistorical: true)` compatibility API retains the old
selection behavior and labels the profile historical.

## Frozen protocol

`pairedMeasurement` declares `calibrationBlocks`, `confirmationBlocks` (6..128),
`orderSeed`, `calibrationSeedStart`, `confirmationSeedStart`, `residentSlots`
(1..32, default 1 for cache-warm compatibility; declare 3 for rotation), `maximumAllocationBytesPerArm`, and
`maximumBaselineDrift` (default 0.15). Defaults are 8+8 blocks, order seed 73019,
and input seed starts 110001/910001. Seed ranges, including every resident slot,
must be disjoint. A block's ring starts at `seedStart + block * residentSlots`.
Both correctness.seed and workload.parameters.seed are changed; actual initialized
buffer hashes and independently computed CPU oracles are retained per slot.

SHA-256 sorting reproducibly permutes challengers inside every block and chooses
ABBA or BAAB for each challenger. A is always the declared baseline. The four
positions contain an AB and a BA comparison. Both arms retain separate resident
rings simultaneously. Every sampled position repeats the declared warmup and then
timestamps exactly `dispatchesPerBatch` complete plans, rotating the ring from
slot zero. Upload, compilation, warmup, poison and verification are excluded;
required execution-plan dispatches, resets and transitions are included.
Rotation is not a cache-cold guarantee. Dispatch count is frozen for all arms and
phases. Batches shorter than `minimumBatchMilliseconds` are retained and rejected;
choose an adequate count in the declared manifest before formal measurement.
Do not change settings after examining confirmation and call the rerun independent.

Every position verifies every resident slot with poison/re-execution/readback.
Raw JSON retains phase, block, position, candidate/arm, order seed, input seed
ring/hashes, timestamps, GPU sample, allocation, DXIL/source/device identities,
verification and failure diagnostics. `paired-observations.csv` is a flat companion.
Failed, short and noisy observations are never silently removed.

The independent unit is a complete four-position block, not an individual plan
dispatch. The statistic is the mean of the block log speedup ratios. The interval
is a two-sided 95% Student-t interval on that mean, exponentiated to a geometric
speedup, using conservative upward-rounded critical values. This assumes
approximately independent blocks and a usable log-ratio mean; it is not evidence
across devices or processes. Calibration ranks eligible candidates; its intervals
are descriptive after searching multiple candidates. Confirmation tests exactly
one frozen selection, so confirmation is not another search over candidates.

All blocks are required. Both raw-arm CVs, paired baseline maximum/minimum drift,
p95 non-regression and the lower confidence speedup bound must pass. A baseline
self-control still reports its interval but only gates correctness, independent
inputs, stability and drift; it does not have to outperform itself. The baseline
median must also stay within the drift budget across phases. External applications,
temperature and clocks are uncontrolled and explicitly recorded as such.

## Selection, resume and deployment

The runner writes the selected candidate and selection-lock hash before the first
confirmation timestamp. Confirmation uses a disjoint declared input range and
freshly sampled timing on newly prepared resources. Failed confirmation emits no
profile and never selects a runner-up using confirmation. No calibration evidence
is presented as confirmation timing.

Paired checkpoints use `hlslperf.paired-checkpoint.v2`. Identity binds the complete
effective manifest, protocol, transitive source, workload implementation, device,
driver, runner/backend and available DXC/Vortice binaries. A complete checkpoint
replays its original report without new timing. An interrupted attempt is archived
as `.historical-<id>.json`, then the entire measurement restarts in a new session.
No old timing is pooled with the resumed session, and failure records remain in
the archived attempt. Historical checkpoints are rejected by the paired runner.

Profiles use schema 3.0 and contain workload implementation, execution identity,
protocol and confirmation hashes; their medians/p95 come from confirmation only.
Unity's consumer also binds the selected candidate and length-prefixed canonical
defines hash, recomputed from the actual defineValues. It requires project-owned expected identities supplied via
`HlslPerfDeploymentIdentity`, exact device+driver, and the confirmed protocol.
Never copy expected identities from untrusted incoming profile JSON. Its optional
`allowHistorical: true` parameter explicitly permits schema 2.0 behavior, with a
historical reason. The consumer validates identities, not an authenticated
hardware attestation or a fresh GPU remeasurement.

## Bounded post-integration validation

The integrator owns formal R9700 evidence on the final merged binary. Freeze a
ledger before execution: one uint-mix control; baseline versus widest valid radix
candidate; dynamic zero/intermediate/maximum count correctness; representative
cache-warm (one slot) and rotating (three slots) scenarios. Start with 8+8 blocks
and two candidates per performance cell, preserve all rejected cells, and use the
shared `Invoke-SerializedValidation.ps1` mutex for every build/GPU measurement.
Run CLI `tune <manifest> --output <directory> --adapter R9700 --rga off`;
`--resume <checkpoint.json>` replays a complete run or archives/restarts a partial
attempt. CLI exit 2 means no deployment profile, not lost measurement evidence.

The worker's short smoke tests plumbing, correctness and rejection behavior. It
does not establish a performance gain, validate the integrated radix/dynamic
candidate set, or satisfy the formal calibrated-versus-confirmed evidence matrix.
