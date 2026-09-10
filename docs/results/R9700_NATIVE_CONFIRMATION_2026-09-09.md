# R9700 native workload confirmation, 2026-09-09

The tiled four-bit sorter reduced time against the FFX implementation supplied
by the pinned GPUSorting native workload at `2^25` pairs. The other six
preregistered comparisons favored the external baseline. GPU correctness passed
for all tested implementations and sizes. These findings support keeping the new
algorithms opt-in; they do not support a general default promotion.

All **70 independent timing processes** completed successfully, with no excluded
results, failed processes or overlapping test processes. Phase one contains four
comparisons with the original four-bit candidate. After it completed, a separately
requested phase tested the eight-bit candidate on all three original sort workloads.
The first plan and results were retained unchanged. No tuning or candidate selection
was performed between these phases.

| Phase / native workload | Elements or pairs | Baseline mean ms | Candidate mean ms | Baseline/candidate ratio, 95% interval |
|---|---:|---:|---:|---|
| 1: GPS RTS / wave-tiled Scan | 268,435,456 | 5.765 | 9.309 | 0.6193 [0.6185, 0.6201] |
| 1: DeviceRadixSort / tile4 | 268,435,456 | 49.708 | 98.723 | 0.5035 [0.4952, 0.5119] |
| 1: OneSweep / tile4 | 268,435,456 | 39.479 | 93.396 | 0.4226 [0.4118, 0.4336] |
| 1: FFXParallelSort / tile4 | 33,554,432 | 15.038 | 12.124 | **1.2404 [1.2400, 1.2408]** |
| 2: DeviceRadixSort / tile8 | 268,435,456 | 48.592 | 185.925 | 0.2614 [0.2589, 0.2639] |
| 2: OneSweep / tile8 | 268,435,456 | 38.872 | 183.552 | 0.2118 [0.2115, 0.2121] |
| 2: FFXParallelSort / tile8 | 33,554,432 | 15.040 | 23.493 | 0.6402 [0.6400, 0.6404] |

A ratio above one favors the candidate. Each absolute mean averages five process
means, each containing the original 100 measured iterations. The reported ratio is
the geometric mean of five within-pair ratios, so it need not exactly equal the
ratio of the displayed arithmetic means. The FFX/tile4 result corresponds to about
19.4% less operation time and 24.0% greater element throughput for this workload.

| Comparison, in the same order | Baseline G elements/s | Candidate G elements/s |
|---|---:|---:|
| RTS / wave-tiled | 46.565 | 28.837 |
| Device / tile4 | 5.400 | 2.719 |
| OneSweep / tile4 | 6.800 | 2.874 |
| FFX / tile4 | 2.231 | 2.768 |
| Device / tile8 | 5.524 | 1.444 |
| OneSweep / tile8 | 6.906 | 1.462 |
| FFX / tile8 | 2.231 | 1.428 |

These are element/pair throughputs, not measured memory bandwidth. The two phases
are independent confirmations; their tile4/tile8 numbers are not a directly paired
comparison with each other. The FFX result is specifically GPUSorting's vendored
implementation. It is not interchangeable with FidelityFX SDK 1.1.4 in the earlier
UnifiedBench adapter, and it does not retroactively replace the September 7 results.

**Method and sources.** The [machine-readable evidence](../evidence/native-runtime-confirmation-2026-09-09.json)
contains the identities, all process rows, correctness counts, allocation snapshots,
and raw evidence locations. The [35 individual pairs](../evidence/native-runtime-confirmation-2026-09-09.pairs.csv)
include both absolute times, execution order and output-log hashes. Recompute the
statistics with `tools/analyze_external_native.py`; it verifies the schedule, raw
logs, input sizes, 100-iteration denominators, throughput arithmetic, exit codes,
device/driver, full-size validation and process non-overlap before aggregating.

Each group uses five independent paired repeats, alternating baseline/candidate
then candidate/baseline order. Intervals use Student-t on log ratios, df=4, critical
value 2.7764451052. They are nominal 95% intervals per comparison, without a multiple
comparison adjustment. There are only five independent process pairs per group;
the 100 inner iterations are not treated as 100 independent processes. The native
programs print aggregate seconds to six decimal places; no per-iteration data was
invented. All recorded values, including slower first pairs, are retained.

**Workloads and boundaries.** GPUPrefixSums commit
`98d93a4e9ed2f3c8353119515bf9be90a2e137ad` supplies the original inclusive
`InitOne`, `BatchTimingInclusiveInitOne(1 << 28, 100)` and validation. The local
exclusive kernel adds original input to its output inside the timed operation.
Reset, inclusive conversion and every barrier remain included.

GPUSorting commit `09a6081d964b682bbb58838b83ab75aebe7f4e05` supplies the original
uint32 ascending key/payload generator, validators and `BatchTiming(count, 100,
10, ENTROPY_PRESET_1)`. DeviceRadixSort and OneSweep retain `2^28`; FFX retains
its own `2^25` default. Each candidate runs the same count, seed sequence, entropy
preset and complete stable pair contract as its paired baseline. Candidate input
interleaving, all radix passes, barriers and direct final SoA scatter are timed.
There is no reduced payload, truncated key width or custom favorable input matrix.

Each process first performs one original full-size validation operation. The
unchanged upstream batch then excludes its first iteration and measures the next
100. Sort seeds are `10 + iteration`, including the excluded iteration. GPU
timestamps surround the complete operation recording hook. Compilation, PSO
creation, buffer allocation, input generation, validation, readback, CPU command
recording, submission and fence waiting are outside the GPU timestamp interval.
This is a GPU operation comparison, not end-to-end application or Unity latency.

The local host selects the exact adapter, separates validate-only from batch-only,
and retains the pinned upstream files byte-for-byte. A static allocation defect was
corrected in a separately generated FFX host source: two uint scratch table element
counts had been passed as byte lengths. The patch multiplies both by four; shaders,
generators, validators and timing loops are unchanged. Its original source SHA is
`4e01d49e72de9194cb042d20f5a8d755b571b1e4ad75f23a68cc5bac3269279f`, patched SHA
`e574a183a8e602e368ef92afc82cc1de4867905ca51e9854e162e57281610797`.
All results are therefore labeled **adapted upstream host**. No unchanged-original
main score, BabelStream score, HeCBench score or universal benchmark ranking is claimed.

**Correctness gate.** The independent managed GPU runner completed 498 configurations
and 2,808 complete-output hash assertions, covering full32 scan overflow, extremes,
tails, multiple partitions, 4/8-bit sort, arbitrary payloads, duplicate-key stability,
keys-only output, empty/small inputs and repeated poison/reset of mutable buffers.
Two calls to verification each perform both `0xA5` and `0x5A` poison attempts.
The initial empty Scan case exercised a sentinel-copy guard. A separate, more exact
empty-host check subsequently verified three original sentinel values four times
each: **12 GPU readbacks, zero reset/dispatch and zero restoration copies**.

The native hosts independently passed **418 strict cases / 1,308 repeated executions**
using CPU-generated full32 inputs, comparison-sort oracles and arbitrary payloads,
plus nine declared full-size GPU validation checks before timing. Every one of the
70 timing processes also passed the original full-size validator before its batch.
Native Scan tests use its aligned input contract; nonaligned tails are covered by
the managed kernel tests. Native validators alone are not used as proof of arbitrary
payload stability or full32 overflow. No GPU correctness or native runtime failure
occurred. An initial C# build missing the required `Axes` initializer was corrected
before GPU execution; no formal result was discarded because of that build correction.

**Device and memory.** Every native process verified AMD Radeon AI PRO R9700,
vendor `0x1002`, device `0x7551`, LUID `76566`, driver `32.0.31041.1004`.
DXGI reported 34,054,242,304 dedicated bytes and a local budget of 33,244,737,536
bytes. The probe confirmed feature level 12.2, shader model 6.7 support and wave
capability 32–64. Local candidates compile for SM6.6 and force wave32. Upstream
wave choices are retained: RTS uses group256 and 12 values/thread, Device/OneSweep
use the pinned generic group256 / seven-key preset because R9700 is absent from
its device table, and FFX uses group256 / four keys with its original group cap.
Those upstream shaders do not force a wave width; actual selected wave width was
not independently sampled. Local radix uses group128 / four records, 4 or 8 bits;
local Scan uses group256 / 16 values, B=4096 and at most 256 persistent groups.

The complete `2^28` buffers were allocated, not simulated. Scan owns two 1 GiB
arrays, the original partition reductions and (for the candidate) 786,440 state
bytes. Native tile sort retains the original four 1 GiB SoA arrays and original
DeviceRadix scratch, plus two 2 GiB AoS arrays and its own histogram/prefix scratch.
Its logical default-heap totals are 8,810,570,820 bytes for tile4 and 9,819,170,820
for tile8, excluding query/readback/driver overhead. The conservative preflight
ceiling was 12 GiB, checked against 75% of remaining budget.

| Native validation allocation | Observed process local usage bytes |
|---|---:|
| RTS, `2^28` | 2,159,861,760 |
| Wave-tiled Scan, `2^28` | 2,160,910,336 |
| DeviceRadixSort, `2^28` | 4,460,441,600 |
| OneSweep, `2^28` | 4,920,508,416 |
| Tile4, `2^28` | 8,823,042,048 |
| Tile8, `2^28` | 9,831,510,016 |
| FFX, `2^25` | 550,825,984 |
| Tile4, `2^25` | 1,113,657,344 |

These are DXGI process usage snapshots, not proof of each resource's residency or
measured cache behavior. A shared `Local\CodexR9700VNextUnityGpu` mutex covered
each hardware stage, with exactly one native child process at a time. No builds,
downloads or other benchmarks ran during timed batches. Unity remained closed.
User applications, GPU temperature and clocks were not controlled or measured;
there were no driver, power, global-cache or system-setting changes.

**Frozen identities and reproduction.** Native build code is
`24591b11ef97c4836a9d5d52719ab58273f32582`. Phase-one source snapshot is
`15d41958d3fd24a1ba1c079b93c04f455cb85b9d`; phase two is
`960a4af999bd3e4e5059c50cecb0661afef2a7ed`. Only the orchestration needed for the
second registration changed between phases. Both used exactly these executables:

- Scan SHA-256: `f75672a6fb73d2289490350770ef6c34b42c02413de022fe3f9890b86729b399`.
- Sort SHA-256: `6ac475641ed97bb3aabd94612f180c35dd57967d8d1b22c70b7acfa100b70624`.
- Phase-one plan SHA-256: `7780f12c53a039a44ed6611873f7a85b8429aacb3aa43e865018531c82cd4cdb`.
- Phase-two plan SHA-256: `794f5a24de1a31d255b5f4b0df7bf57399ed2bd9f16f4271ad888e4026e08417`.

The build uses pinned native DXC 1.8.2403.18, Agility SDK 1.613.0, WIL
1.0.240122.1 and VS 2026 v145. Three C4244 warnings remain in unchanged upstream
DeviceRadixSort source. Native source, shaders, runtime libraries, patched source,
driver and workload identities are retained in the registration and build receipts.
The later empty-input and statistical-audit tool changes are in
`82e37c4ef611a0bacc38b56ba0a8d31432fc725f`; they did not alter measured native code.

All raw logs, per-process exit records, registrations, GPU correctness details and
analysis inputs remain under this absolute local directory:

`C:/Users/EdwinLiu/Documents/Codex/2026-09-08/ni-sh/work/wt/hlsl-integration/.scratch/runtime-20260909`

The exact native binaries, build logs and allocation patch remain under
`.scratch/native-runtime-01` in the same worktree. The checked-in JSON gives the
absolute paths and hashes needed to locate and verify the data. The entry points
are `tools/run_external_native.py validate`, `register`, and `confirm`; the latter
requires an unchanged preregistered identity. Run hardware stages under
`tools/Invoke-UnifiedValidationLock.ps1`. Reanalysis is CPU-only:

```powershell
python tools/analyze_external_native.py --root .scratch/runtime-20260909 --output .scratch/native-reanalysis.json
```

The September 8 Unmeasured receipts and source locks remain unchanged. No kernel
or SUMMIT consumer asset changed during this execution phase; canonical asset
SHA `117f226d4f83c9a19155d03f2d6179e55db676e19fc58c76ccb1aaef836b6f16` remains
valid. This native result is not a Unity dispatch/import result or a new deployment
profile. Assessment: **share with the stated workload, identity and single-device
caveats**. There is no unresolved failure blocking these seven comparisons.

The release check at `2026-09-09T09:45:12Z` found no native test process, managed
validation runner or Unity process. MSBuild and C# compiler servers shut down
successfully, and the shared GPU mutex was immediately available. The local
`process-release.json` record and its SHA-256 are referenced in the checked-in evidence.
