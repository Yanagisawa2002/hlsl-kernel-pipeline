# Crowd whole-task comparison, version 2

Status: **CPU conventional arm and version-2 executor implemented; each new
build still requires the bound acceptance gates below before performance**.
The version-1 discovery/freeze/confirm commands remain retired. No performance
result is implied by source readiness or by a non-timed correctness run.

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
| Primary confirmation | Only candidate and frozen comparator; freeze 8/12/16/24/32 paired rounds after discovery using the fixed precision rule below; alternating AB/BA, one new process per arm per round |
| Independent unit | A pair of process-level lifetime costs. Twelve requests inside a process are correlated; they are not twelve independent replications |
| Primary uncertainty | Two-sided 95% Student-t interval, df=n-1, on the frozen n paired log(candidate/comparator) costs, then exponentiate |
| Evidence of lower cost | Upper endpoint below 1 with all correctness/resource/identity gates passing. Also report absolute milliseconds saved and the interval. An inconclusive interval remains inconclusive |
| Scope | This controlled renderer, input cohort, 12-request reuse model and exact Windows/D3D12/CPU/GPU configuration |

Discovery has two independent processes per GPU arm and CPU worker setting on
each of the four cases. Test CPU workers 1, 2, 4, 8, 12, plus the maximum feasible
frame-level count when different; the maximum is min(12, .NET CPU quota). The
primary case is first, so an initial limited discovery can stop after its 18
processes on the current 20-logical-CPU host. Full registration requires all 72
planned discovery processes. Each invocation starts at most two processes by
default; explicit prior batch paths continue the fixed order. It creates no
scheduler and waits for no future idle period.

Freeze the alternative with the lowest median large-case lifetime cost,
including every CPU worker setting. From the two paired discovery log ratios,
estimate n = ceil((2.4 * sample_sd / log(1.05))^2), with minimum 8. Round up to
the next of 8, 12, 16, 24 or 32; refuse registration above 32. This is an
uncertain two-pair pilot estimate targeting roughly 5% relative interval
half-width, not a power guarantee. The exact n and stopping rules are written
before confirmation. Do not extend or stop early based on confirmation results.

Confirmation collects that one primary contrast and two descriptive pairs for
each adverse case (2*n + 12 processes total). Keep both processes of a pair in
the same invocation; alternating AB/BA balances order. Confirmation uses the
second seed cohort, unused in performance discovery or worker selection.
Source, outputs, device/runtime, gates, selected workers and all prior batch
receipts are frozen. Any failed observation invalidates the cohort. Discovery
may resume after a preflight that launched nothing, with that failed preflight
retained; confirmation treats any failed invocation as a stopped attempt.

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

The implemented `cpu-frame-parallel` arm precomputes the immutable mask
eligibility, motion origins, colors and background once. First-use timing
includes this preparation and the shared seed generator. At each new frame it
computes motion and directly splats each visible agent's small integer glow into
that frame's own pixels. It uses the original saturating integer color semantics
and preserves stable visible seeds for independent diagnostics. Tile binning is
not needed by this direct CPU algorithm and is not fabricated as CPU work or
reported as a CPU output. GPU tile/list invariants remain separately required.

Frames run on bounded Parallel.For workers. All workers share immutable cached
agents/background and write disjoint output frame ranges. No N-sized per-worker
scratch is allocated; increasing to 12 workers does not multiply the logical
storage. Record both configured workers and observed participating/peak workers;
a requested limit is not a claim of exclusive physical CPU use.

The earlier 512 MiB value was an experimental safety budget, not a caller hard
constraint. Version 2 allows **2 GiB of logical buffers per memory domain**, with
at least 4 GiB free host RAM and 8 GiB free VRAM at the campaign gate. Report actual
logical buffers and process peak working set separately; allocator/driver/CLR
memory is not all represented by logical bytes. The CPU preparation uses two
bounded passes over input to allocate exactly the mask-eligible cache, then
reuses it. It never reduces workers to fit a memory cap. Validate the highest
reasonable count explicitly in every case; if it cannot run, keep eligibility
incomplete or register a declared constrained-memory sensitivity study.

The CPU and GPU algorithms differ deliberately as complete-task alternatives.
A result cannot isolate scan speed, parallelism, static caching or CPU-versus-GPU
hardware from one another. Immutable-input caching is permitted by this caller;
it would need reevaluation for frequently changed seeds or masks. The serial
oracle remains unchanged and independent from the new runtime implementation.
CPU managed cleanup releases references and uses normal CLR collection; no
forced GC, affinity, priority, cache or power changes are introduced.

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
- The version-2 performance implementation requires these bound receipts;
  a source/binary snapshot beside an older validation file is insufficient.
  Native runtime hashes from the actual process must agree across gates and
  discovery/confirmation. The version-2 runner checks these prerequisites and binds every timed process
  to its exact protocol cell; the retired runner cannot launch measurements.
- Build/correctness and performance retain their separate campaign gates.
  Performance requires CPU <=25% and GPU <=15%, as well as mutex ownership,
  queue order, memory/disk budgets and no competing heavy process. A failure
  stops the attempt without deleting it or replacing observations.

The Windows/D3D12 route remains selected. No Vulkan/CUDA rewrite, remote
Linux/5090 execution, host installation or hardware setting change is part of
this repair. A backend or device change requires new implementation validation
and independent results.

Filter interpretation follows the [D3D12 filter structure](https://learn.microsoft.com/en-us/windows/win32/api/d3d12sdklayers/ns-d3d12sdklayers-d3d12_info_queue_filter) and [denied-message counter](https://learn.microsoft.com/en-us/windows/win32/api/d3d12sdklayers/nf-d3d12sdklayers-id3d12infoqueue-getnummessagesdeniedbystoragefilter). The actual active filter is recorded from the local device; a default is never assumed.
