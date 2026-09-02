# v0.6 GPU primitive pack

v0.6 turns the original scan benchmark into a reusable set of composable GPU
building blocks. Every number measures a complete `KernelExecutionPlan`, not an
isolated instruction or CPU submission time.

## Algorithms

### Generic exclusive scan

The shared scan library supports four associative uint operators:

| Define | Operator | Identity |
|---:|---|---:|
| 1 | wrapping add | 0 |
| 2 | minimum | `UINT_MAX` |
| 3 | maximum | 0 |
| 4 | XOR | 0 |

Add uses native wave prefix intrinsics. Other operators use a bounded shuffle
scan, then the same group hierarchy or persistent decoupled-look-back backend.
Candidate axes cover Blelloch, wave-hybrid, and single-pass backends, Wave32/64,
scalar/`uint4` I/O, items per thread, logical-block scale, and persistent group
count.

### Segmented scan

A segment is represented by the associative descriptor `(tail, hasHead)`. For
two consecutive ranges `A` and `B`:

    combine(A, B) = (B.hasHead ? B.tail : A.tail + B.tail,
                     A.hasHead | B.hasHead)

The same monoid scans items inside a thread, thread descriptors inside a wave,
wave descriptors inside a group, and block descriptors during decoupled
look-back. This avoids a special CPU or multi-dispatch fix-up path for segment
boundaries.

### Stream compaction and fusion

Backends 1 and 2 execute the conventional pipeline:

    predicate producer -> materialized flags -> exclusive scan -> scatter

Backend 3 fuses producer, scan, and consumer into one persistent data dispatch
plus an O(1) epoch reset:

    input -> local flags/prefix -> block look-back -> direct compacted output

The fused path never materializes the N-element flag and prefix arrays and does
not recompute the predicate in scatter. Its extra global state is O(blocks), not
O(elements). This is the important end-to-end claim: the speedup comes from
removing full-buffer traffic and pass boundaries, not merely making `scan`
faster in isolation.

### Histogram plus prefix offsets

The baseline performs one global atomic per item. The optimized backend builds
replicated group-local histograms in LDS, merges only bin totals globally, then
performs a 256-bin exclusive prefix to produce offsets plus a terminal count.
Replica count is an explicit resource/occupancy tradeoff: more replicas can
reduce local contention while consuming more LDS and adding merge work.

### Radix sort

The first radix backend is a correctness-first stable binary LSD sort. Each of
32 bits runs zero-flag production, the shared exclusive scan, and stable scatter.
Ping-pong key buffers and one scan scratch hierarchy are reused for every bit.
It is deliberately a base for future wider-radix fusion, not a claim to replace
specialized vendor sorting libraries today.

## Measured poison-verified snapshot

These cache-warm D3D12 runs used an AMD Radeon AI PRO R9700, driver
32.0.31041.1004, Vortice.Dxc 3.8.3.0, and the checked-in manifests. They use the
post-timing poison/re-execute correctness gate. All selected candidates passed
the 5% CV stability gate. Parameters and speeds are specific to this
GPU/driver/compiler combination.

| Workload | Elements | Candidates correct | Baseline median | Selected median | Selected p95 | Speedup |
|---|---:|---:|---:|---:|---:|---:|
| generic XOR scan | 4,000,000 | 66/66 | 0.05258 ms | 0.02773 ms | 0.02788 ms | 1.8963x |
| segmented add scan | 4,000,000 | 108/108 | 0.06989 ms | 0.04489 ms | 0.04525 ms | 1.5571x |
| producer-scan-scatter compaction | 4,000,000 | 66/66 | 0.09433 ms | 0.01911 ms | 0.01931 ms | 4.9357x |
| histogram + offsets | 4,000,000 | 36/36 | 0.17234 ms | 0.01068 ms | 0.01081 ms | 16.1422x |
| 32-bit radix sort | 262,144 | 18/18 | 0.32861 ms | 0.25800 ms | 0.25980 ms | 1.2737x |

The candidate totals are 294/294 correct. One non-selected compaction candidate
and one non-selected radix candidate were noisy and were excluded from
deployment selection.

## Evidence integrity lesson

During development, a fused reset pass was accidentally bound to the wrong UAV.
The first invocation produced the correct result, later timed invocations did no
work, and a conventional final hash still saw the first result. The apparent
65.55x speedup was discarded.

That failure led to a backend-wide gate: after timing, HlslPerf poisons every
byte of the verified resource, invokes the already-used plan once, and hashes
the rebuilt output. The corrected fused run remained correct and retained a
large real advantage; stale-output timing can no longer pass for built-in or
external workloads.

## Current limits

- The published snapshot covers one GPU and one driver; it is not a universal
  hardware ranking.
- Timings are cache-warm steady-state measurements and do not include upload,
  readback, or CPU submission fences.
- The radix implementation is one bit per pass and sorts keys only.
- Segmented scan currently implements wrapping addition; lifting its descriptor
  to all generic operators is a valid extension.
- Static RGA resource data and real runtime occupancy/counters are different;
  the latter remain v0.7 work.
