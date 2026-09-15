# Crowd whole-task comparison, version 2

Status: **design prepared; CPU conventional arm and version-2 performance
orchestration are not implemented**. The version-1 discovery, freeze and confirm
commands now fail before launching anything. The old 128-process schedule must
not run. This document replaces its acceptance and comparison rules; it is not
an experiment result or a frozen registration.

## Question and single primary comparison

The question is whether the **wave-tiled exclusive** application path reduces
the existing CPU-input-to-RGBA-export task's cost against the strongest eligible
alternative. The candidate is fixed as `wave-tiled`. Discovery may return NO-GO;
it must not change the candidate after seeing confirmation outputs.

| Decision | Predeclared rule |
|---|---|
| Primary case | `large`: N=8,388,608, visibility mask 511, 480 x 270, 12 frames/request |
| Primary metric | Actual accumulated application work for a 12-request lifetime, divided by 12: first use + requests 1..11 + resource cleanup |
| Included costs | CPU seed generation, plan/device/compile/PSO/allocation/upload, rendering, readback, CPU copy, each buffered RGBA write and close, cleanup |
| Timing boundary | Existing application timer; command parsing, evidence hashing, oracle verification gaps and OS process startup are outside it. It is not process wall time or durable-storage latency |
| Comparator pool | `hierarchical`, `fused`, pinned upstream `rts`, and the conventional CPU renderer defined below; every eligible arm must be measured in discovery |
| Comparator selection | Lowest median process lifetime cost on the discovery seed; exact ties use the listed pool order. Retain every arm and all failed attempts |
| Primary confirmation | Only candidate and frozen comparator, 8 paired rounds on the untouched confirmation seed; 4 AB and 4 BA rounds alternating, one new process per arm per round: 16 processes total |
| Independent unit | A pair of process-level lifetime costs. Twelve requests inside a process are correlated; they are not twelve independent replications |
| Primary uncertainty | Two-sided 95% Student-t interval, df=7, on 8 paired log(candidate/comparator) costs, then exponentiate |
| Evidence of lower cost | Upper endpoint below 1 with all correctness/resource/identity gates passing. Also report absolute milliseconds saved and the interval. An inconclusive interval remains inconclusive |
| Scope | This controlled renderer, input cohort, 12-request reuse model and exact Windows/D3D12/CPU/GPU configuration |

The 8-pair count is a fixed initial design, not a guarantee of adequate power.
Discovery must estimate variance and expected runtime before registration. If
8 pairs are unlikely to resolve a useful effect, report that limitation or
register a revised sample size **before** confirmation. Do not extend a run
because its current interval narrowly misses the desired result.

Discovery must include at least two independent processes per eligible arm on
each of the four existing cases. Resource-feasible CPU worker choices are tuned
only within discovery. Freeze exact implementations, worker count, all build and
gate receipts, raw reference hashes, device/runtime, metric, pair/order, sample
size and stopping rules in a new version-2 registration before confirmation.
No script in this change implements or starts that schedule.

## Multiple comparisons and adverse cases

There is **one confirmatory hypothesis**, the primary comparison above. Small,
medium-tail and dense-tail cases, first use, steady requests, render/readback
components, memory and other arm comparisons remain descriptive diagnostics.
Their means, individual process values, spread and adverse outcomes must be
reported, but a nominal interval excluding 1 cannot establish an additional
benefit claim. The legacy analyzer is marked `inferentialClaimsEligible=false`.

If a later protocol needs several confirmatory claims, enumerate their complete
family before collecting confirmation and use simultaneous Bonferroni intervals
at confidence `1 - 0.05 / family_size`, with the correct degrees of freedom.
Do not inspect 24 nominal pair/case intervals and report whichever looks best.
Changing the primary metric, candidate or comparator requires a new untouched
confirmation cohort and registration. Never reclassify a rehearsal as discovery.

Always publish single-request first use and the 12-request reuse costs together.
A steady GPU improvement with worse complete lifetime cost is not a whole-task
win. CPU, memory and integration tradeoffs remain explicit; a positive interval
does not automatically promote any implementation to a default.

## Why a conventional CPU renderer is applicable

The caller generates seeds on the CPU and consumes CPU-resident RGBA files. A
CPU renderer can satisfy both endpoints without device setup or PCIe transfer.
There is no current GPU-resident-input or presentation contract that excludes
it. Therefore omitting CPU would limit any conclusion to choosing among GPU
implementations, not choosing the best whole-task implementation. The original
serial CPU oracle is useful for correctness; its time is not a strong baseline.

The proposed `cpu-frame-parallel` arm must:

1. Use the identical seed generator, integer visibility/motion, stable selected
   sequence, tile membership and additive integer raster semantics. Produce all
   144 advancing frames and the same twelve full RGBA files.
2. Run independent frames on a bounded worker pool. Each worker reuses its
   visibility/bin/scatter workspace; reset counts/cursors and reuse allocations.
   Scan seeds sequentially within each frame, retain stable IDs and use prefix
   offsets to bin visible seeds. Avoid a per-pixel scan over all agents.
3. Use the same optimized Release compiler settings. Do not serialize an
   otherwise parallel workload or add a GPU upload/readback to the CPU arm.
   Vectorization or cached per-agent coordinates may be used if byte-exact.
4. Bound workers by available CPU quota, 12 independent frames, and a declared
   512 MiB host input/output/scratch budget. For two N-word arrays per worker,
   compute `floor((budget - input - atlas - shared) / worker_bytes)` and report
   the actual value. At the largest N this prevents allocating 12 full-size
   pairs of arrays. Report runtime/allocator overhead separately from logical
   buffers, and process peak working set; this is not a total-memory guarantee.
5. Consider resource-feasible counts from 1, 2, 4, 8 and 12 in discovery only,
   including the maximum feasible count. Freeze the best count from discovery;
   do not use the machine's logical CPU count as an exclusive-resource claim.
6. Start first use before generation and allocation, include computation and
   exports, reuse buffers for requests 1..11, and account for cleanup. Validate
   outside timing against the independent unchanged scalar oracle, including
   boundary/tail/empty/dense masks, stable lists/bins and every output pixel.

Implementation and independent correctness of this CPU arm are still required.
Until they exist, the comparator pool is incomplete and formal registration is
not ready. Lack of the CPU arm cannot be interpreted as a measured GPU benefit.

## Evidence prerequisites and failure handling

- `crowd_build_evidence.py build` requires a clean commit, performs an actual
  non-incremental offline rebuild, and records command/PID/timestamps/exit/log
  hashes, all tracked input hashes, runner/native and CPU-test binary hashes.
- `check` requires that same clean commit and exact built files before and after
  its child process. The runner records its own assembly hash, PID and embedded
  build commit outside application timing. Output manifests and input reference
  hashes are sealed in each check receipt. Failed attempts retain their logs.
- Boundary and full-scene checks require complete structured debug evidence:
  queue available, zero discarded messages, stable stored/retrievable counts,
  all stored messages retained, and no Error/Corruption severity. Record both
  filters and the denied counter. Only an empty allow list and severity-only
  denial of Info/Message are eligible; category/ID filters, Warning/Error denial
  or filters changing during a read fail. This preserves the default exclusion
  of informational object-lifetime messages without confusing it with overflow. Drain after
  every output check and after executor disposal. A dedicated control device
  must prove deliberate queue overflow and injected errors are rejected.
- The frozen performance implementation must require these bound receipts;
  a source/binary snapshot beside an older validation file is insufficient.
  Native runtime hashes from the actual process must agree across gates and
  discovery/confirmation. This future enforcement is a prerequisite for the
  version-2 performance runner, not a capability of the retired runner.
- Build/correctness and performance retain their separate campaign gates.
  Performance requires CPU <=25% and GPU <=15%, as well as mutex ownership,
  queue order, memory/disk budgets and no competing heavy process. A failure
  stops the attempt without deleting it or replacing observations.

The Windows/D3D12 route remains selected. No Vulkan/CUDA rewrite, remote
Linux/5090 execution, host installation or hardware setting change is part of
this repair. A backend or device change requires new implementation validation
and independent results.

Filter interpretation follows the [D3D12 filter structure](https://learn.microsoft.com/en-us/windows/win32/api/d3d12sdklayers/ns-d3d12sdklayers-d3d12_info_queue_filter) and [denied-message counter](https://learn.microsoft.com/en-us/windows/win32/api/d3d12sdklayers/nf-d3d12sdklayers-id3d12infoqueue-getnummessagesdeniedbystoragefilter). The actual active filter is recorded from the local device; a default is never assumed.
