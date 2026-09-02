# Measurement methodology

The pipeline separates an observed fast result from a deployable profile. A
candidate is not deployable merely because one sample is lower.

## Measurement contract

1. DXC compiles every distinct pass entry point with O3, strict mode, and
   warnings as errors.
2. A workload provider supplies deterministic input and an independent CPU
   oracle. Every candidate must match the verified output byte for byte.
3. Each candidate receives at least 75 ms of warm-up in the bundled manifests.
4. The runner doubles complete plan executions per timestamp batch until the
   interval reaches at least 10 ms, capped by the manifest. A plan includes all
   dispatches and required inter-pass resource barriers.
5. Upload, command submission fences, timestamp resolve, readback, and CPU work
   remain outside the measured interval.
6. Fifteen independent batches produce median, interpolated p95, sample standard
   deviation, and coefficient of variation (CV). Bundled manifests require CV
   at or below 5%.
7. The observed fastest stable candidate must be at least 1.01x faster than the
   declared stable baseline and must not regress p95. Otherwise selection retains
   the baseline.

The showcase and stress-grid runners add a stricter comparison-level gate: both
the declared baseline and selected candidate must be stable. A noisy pair is
remeasured up to three times and each rejected raw run is retained for audit.
The four-level showcase refuses an unstable level; the stress grid renders an
unstable cell as neutral gray and excludes it from budget-crossing selection.
This prevents a transiently slow baseline from becoming an inflated A/B claim.

GPU timing is never cached. DXIL is cached by source hash, entry point, shader
model, compiler version, compiler option schema, ABI, and sorted defines.

## Visual A/B contract

The optional D3D12 verified-output callback copies the already-mapped oracle
buffer after hashing. It does not widen the timestamp interval. The standalone
showcase uses that callback to retain literal GPU RGBA frame atlases for the
declared baseline and selected candidate.

Visual comparisons run candidates sequentially with identical deterministic
inputs. Composition aborts on any byte difference. GIF playback may pace atlas
phase by measured median time to make a throughput difference visible, but it
does not alter pixels, invent quality differences, or substitute CPU timing for
GPU timestamps.

The budget-crossing mode is stricter than median-paced playback. It submits one
logical update per requested deadline and replays each candidate's recorded GPU
sample sequence through a serial completion queue. The displayed completed,
backlog, and missed-deadline counts are derived from those samples. The two
atlases must still be byte-identical; no synthetic sleep, duplicated quality
setting, or fabricated duration is used.

For the single-pass backend, the constant-cost epoch/reset dispatch is inside
every timed plan execution. “Single-pass” means one dispatch traverses the data
and writes the final scan; it does not mean setup is hidden outside timestamps.

## Static evidence contract

RGA runs after measured candidates pass correctness. Its DX12 live-driver mode
compiles the same source, entry point, target, shader model, and defines. The
report stores per-entry-point VGPR/SGPR usage, physical/available counts,
LDS/scratch bytes, thread-group dimensions, ISA path, and live-VGPR used/allocated
summary.

RGA resource data is explanatory evidence. It never participates in winner
selection because lower register count is not itself lower execution time.
Likewise, no overall occupancy number is inferred when RGA does not emit one.

## Profile invalidation

The compatibility key includes adapter identity, driver version, backend,
shader model, compiler version, manifest hash, and kernel hash. Schema 2.0 also
stores workload and ABI ids. The Unity adapter rejects workload/ABI mismatches
even under its least strict device policy.

## Limits of the current evidence

- Results cover one Windows/D3D12 GPU and compute queue.
- Repeated input makes the reported kernels cache-warm steady-state tests; the
  numbers are not a DRAM-bandwidth benchmark.
- Candidate blocks are not interleaved or randomized and no confidence interval
  is reported yet.
- Energy, temperature, and runtime wave occupancy require dedicated telemetry or
  an RGP/runtime-counter adapter.
- The workload pack covers reusable primitives, not a complete application frame.
- The particle showcase is a controlled compute-and-visualization plan, not a
  claim about an entire Unity or game frame.

These limits are explicit so the tool remains an optimization pipeline rather
than a benchmark-marketing generator.
