# RTX 4090 native inclusive scan confirmation — 2026-09-15

Native inclusive output reduced the complete GPU scan operation from **6.376 ms
to 2.904 ms**, a **54.45%** reduction against the previous exclusive scan plus
conversion implementation. The pinned GPUPrefixSums RTS arm took **3.584 ms**;
the fused local adaptation reduced its operation time by **18.97%** on this machine.

All 18 preregistered fresh processes passed the original full-size validator.
No formal process failed, overlapped another benchmark process, or was excluded.
No scan parameter, input size or timing boundary changed during confirmation.
This supports this specific RTX 4090 / inclusive uint32 / `2^28` workload.
Application end-to-end latency and performance on other GPUs remain unmeasured.

## Results

| Arm | Mean GPU operation ms | 95% interval for process mean ms | G elements/s |
|---|---:|---|---:|
| `tile`: original exclusive + AddInput | 6.376178 | [6.375019, 6.377338] | 42.100 |
| `tile-fused`: native inclusive | **2.904103** | [2.902499, 2.905707] | **92.433** |
| `rts`: pinned GPUPrefixSums ReduceThenScan | 3.584187 | [3.583652, 3.584722] | 74.894 |

| Paired comparison | Baseline / fused ratio, 95% interval | Time reduction, 95% interval | Paired time saved ms, 95% interval |
|---|---|---|---|
| Original / fused (primary) | 2.195576 [2.194248, 2.196905] | **54.4539% [54.4263%, 54.4814%]** | 3.472075 [3.469968, 3.474182] |
| RTS / fused | 1.234180 [1.233559, 1.234802] | **18.9746% [18.9337%, 19.0154%]** | 0.680083 [0.678637, 0.681530] |

The old adapter remains slower than RTS: RTS / old is 0.562121
[0.562007, 0.562236]. The complete three-arm comparison is retained. Throughput
is elements divided by GPU operation time; it is not measured memory bandwidth.

### Every formal process mean

Each cell below is a separate process running 100 measured iterations after the
unchanged one-iteration warmup. The order covers all six permutations.
[CSV with PIDs, timestamps and log hashes](data/rtx4090-inclusive-2026-09-15/analysis.csv) ·
[Full audited analysis and per-process events](data/rtx4090-inclusive-2026-09-15/analysis.json).

| Round | Process order | Old ms | Fused ms | RTS ms |
|---|---|---:|---:|---:|
| 1 | tile → tile-fused → rts | 6.37459 | 2.90492 | 3.58411 |
| 2 | tile → rts → tile-fused | 6.37596 | 2.90297 | 3.58385 |
| 3 | tile-fused → tile → rts | 6.37574 | 2.90255 | 3.58430 |
| 4 | tile-fused → rts → tile | 6.37797 | 2.90314 | 3.58438 |
| 5 | rts → tile → tile-fused | 6.37653 | 2.90661 | 3.58499 |
| 6 | rts → tile-fused → tile | 6.37628 | 2.90443 | 3.58349 |

Arithmetic means use six process means per arm. Ratios use the geometric mean of
six within-round baseline/candidate ratios. All intervals are two-sided nominal
95% Student-t intervals, df=5; ratio intervals use log ratios. The 100 inner
iterations are not independent samples. Intervals are not adjusted for multiple
comparisons and describe this short, fixed-condition confirmation window.
Upstream prints only aggregate seconds to six decimal places; individual iteration
timings and p95 latency are unavailable. No best-run selection was performed.

## Implementation and ownership

The kernel adds one explicit `HLSLPERF_WAVE_TILED_INCLUSIVE=1` branch while its
input vector is already loaded: `[x, x+y, x+y+z, x+y+z+w]`. The default exclusive
prefix, wave and partition totals, shared spine, full32 split state, fallback,
reset, barriers, and vector/scalar-tail stores are retained. Compaction requires
exclusive offsets and rejects the inclusive define. The removed full-array
AddInput pass logically read input and output and rewrote output: **3 GiB** at
the main size. This is source-level traffic accounting; the paired timings measure
the whole operation, including any difference in local prefix instructions.

`PrimitiveOperations.InclusiveScan(assetRoot, input)` exposes a real caller-input
SDK plan. `ExclusiveScan(..., ScanImplementation.WaveTiled)` explicitly selects
the same implementation with exclusive output. Existing exclusive defaults remain
unchanged. Separate shader IDs permit both modes in one executor. No deployment
profile, Unity integration, or default promotion is produced.
[API, buffers, empty behavior and exact commands](../integration/SCAN_INCLUSIVE.md).

This is an extension of the existing **GPUPrefixSums-derived local adaptation**,
not an original scan algorithm. Thomas Smith's authorship, MIT attribution,
fixed commit `98d93a4e9ed2f3c8353119515bf9be90a2e137ad`, and complete upstream
licenses remain intact. All 114 locked third-party files are byte-identical.
The external RTS shader, generator, validator and timing loop are unchanged.

## Workload, timing and freeze

All arms use the original inclusive `InitOne`, `2^28 = 268,435,456` uint32
elements, and `BatchTimingInclusiveInitOne(count, 100)`. The actual 1 GiB GPU
input readback was exported and hashed, then independently compared to the
all-one byte sequence. Each process first performs the unchanged original
full-size inclusive validation. The original batch then excludes its first
iteration and accumulates the following 100. Input generation still occurs
before each operation according to the upstream loop.

The upstream `TimeScan` GPU timestamps surround the complete operation recording
hook: reset, all barriers/transitions, scan, and AddInput for the old arm.
Compilation, allocation, generation/upload, validation/readback, CPU recording,
submission and fence wait are outside this GPU interval. Both local arms retain
group256, wave32, 16 values/thread, B=4096, four bounded polls and at most 256
persistent groups. RTS retains group256, 12 values/thread and B=3072.

The candidate was not tuned. Two exploratory attempts, each containing one
process per arm, preceded registration. The first exposed a driver metadata
issue in the managed harness; the second followed its repair. Both sets remain
in the raw evidence and are excluded from formal estimates:

| Exploratory attempt | Old ms | Fused ms | RTS ms |
|---|---:|---:|---:|
| 01 | 6.42365 | 2.90454 | 3.58482 |
| 02 | 6.39827 | 2.90272 | 3.58429 |

The scan shader, native executable and candidate settings did not change between
these attempts. The final declaration was registered at **05:18:07.642 UTC**;
the 18 formal processes ran from **05:18:07.869 to 05:18:27.358 UTC**.
[Frozen plan](data/rtx4090-inclusive-2026-09-15/registration-01/plan.json) and
[original process receipts](data/rtx4090-inclusive-2026-09-15/confirmation-01/confirmation.json) retain
source, binary, runtime, device, input, schedule and log identities.

## Correctness gates

- **188/188** existing and extended .NET CPU tests, plus 12/12 deterministic
  scan model tests, 5/5 external source/entrypoint tests and two statistical
  analysis tests with five malformed-evidence controls.
- **37** DXC contracts: 26 accepted shader compilations and 11 expected
  rejections. Wave32/64 layouts, tails, reset, compaction and legacy entries
  retain their checks. Four old/new exclusive/compaction DXIL controls are
  **byte-identical to main**. The SDK compile-only gate compiled 28 entries.
- Final GPU gate: **229 configurations / 936 complete-output assertions**,
  with exact byte comparison and independent uint64/modulo-2^32 CPU oracles.
  Coverage includes random full32 values, maximum uint32, high-bit extremes,
  zero/small/nonaligned counts, multiple partitions, unchanged inputs,
  repeated `0xA5`/`0x5A` poisoning, stable compaction, real empty kernel
  dispatches, and forced missing-predecessor fallback. One scratch allocation
  is reused through shrinking/growing counts and alternating modes; every
  intermediate output is captured and checked.
- Each native arm passed the unmodified **6,160/6,160 upstream TestAll cases**,
  plus 27 independent full32 cases with four inclusive/exclusive executions
  each: **81 strict cases / 324 executions** across three arms. Each arm also
  passed the original full `2^28` validator. Every formal process repeated
  that full-size validator before timing.

All comparisons are exact; no input or tolerance was reduced. Native tests keep
upstream's vector alignment contract; managed tests cover exact nonaligned tails.
The SDK empty guard regenerates a four-byte zero sentinel without shader dispatch.
Separate low-level empty scan tests preserve three existing sentinels with an
actual kernel dispatch and eight-byte scratch. Wave64 received compiler/model
coverage only; this RTX 4090 reports wave32 exclusively.

## Environment and identity correction

- Windows 11 Home, build 26200; Intel Core Ultra 7 265K, 20 cores / 20 logical
  processors; 33,682,857,984 bytes physical RAM.
- NVIDIA GeForce RTX 4090, vendor `0x10de`, device `0x2684`, LUID **99768**;
  NVIDIA driver **591.86**, DXGI version **32.0.15.9186**. Feature-level query
  returned 12.2, shader-model query capped at 6.7 returned 6.7, wave range 32–32.
  Local scan compiles as SM6.6; upstream RTS retains its SM6.7 host choice.
- NVIDIA reported 24,564 MiB total and 22,306 MiB free before this stage.
  DXGI reported 25,310,527,488 dedicated bytes and a 24,505,221,120-byte local
  budget, with 7,405,568 bytes process usage at probe. These are different
  API accounting views, not per-resource residency measurements.
- Allocated process local usage was 2,164,371,456 bytes for both local arms and
  2,163,519,488 bytes for RTS. The full arrays were allocated. Both local arms
  additionally own the same 786,440-byte lookback state.
- Native build: MSVC v143, tools 14.44.35207 / compiler 19.44.35222.0;
  Windows SDK 10.0.26100.0; DXC 1.8.2403.18, Agility 1.613.0, WIL
  1.0.240122.1. Managed build: official .NET SDK 10.0.302 from an isolated
  local download verified by SHA-512; Vortice.Dxc 3.8.3.0. DLL/compiler hashes
  are retained independently for native and managed validation.

The existing managed fingerprint queried `CheckInterfaceSupport<ID3D12Device>`,
which DXGI rejects, then found a stale 581.42 registry installation matching the
same GPU IDs. It now queries `IDXGIDevice`, as
[Microsoft documents for active driver-version queries](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiadapter-checkinterfacesupport).
This matched native DXGI and NVIDIA queries; the full managed/native gates were
rerun before registration. The original passing GPU outputs and stale metadata
are retained as attempt 01. No installed driver or registry setting was changed.

All heavy builds, GPU checks, formal processes and final archive verification used
`Local\CodexR9700VNextUnityGpu`. BabelStream completed and released the hardware
before these GPU stages. The shared lock covered the full scan confirmation and
prevented overlap with the other tasks' benchmarks and builds.
User applications and dynamic clocks were left in their normal state. Idle
snapshots were 44°C before and 41°C after; clocks/temperature during individual
iterations were not sampled. There were no power/driver setting changes or
remote-server actions.

The historical R9700 values (RTS 5.765 ms; adapter 9.309 ms) supplied motivation
only. They are not mixed into any estimate here, and this result does not
establish an R9700 or cross-GPU improvement.

## Source, binaries, raw evidence and reproduction

- Fresh main at task start: `b52d18aff221f3f8822ddb54bc679c9e03932e2a`.
- Measured code: `c2196851bfba3c4f9bf5279e98e829ae70a0cd48` (implementation
  `070a53f` plus the active-driver fingerprint repair). The final branch also
  contains this report, evidence and a reproduction wrapper; measured sources
  and runtime hashes are checked again before delivery.
- Native executable SHA-256:
  `1a69baeb3d31d95485a45602f579f45af81c3ea292a88d8cd07b2b2a18cd6d8a`.
- Full 1 GiB input SHA-256:
  `3d20e9cda21f4b5dda21b48a72446c778d5aa92df8c2f5aa9a0e656a78d3093a`.
- Evidence archive SHA-256:
  `639cabc6c56c0629a000734cdc311403fe9c5a753d97eb9ee0d814d64e38838b`.

The checked-in [raw evidence](data/rtx4090-inclusive-2026-09-15/identity-audit.json) includes every process
log/status, declarations, validation, build/lock logs, compiler receipts,
hardware snapshots and analysis. The local output archive
`rtx4090-inclusive-scan-evidence.zip` is 24,847,353 bytes and contains **500
payload files**, including the losslessly compressed complete 1 GiB input,
exact native/managed binaries, shader artifacts and measured source snapshots.
Every archived payload was decompressed and SHA-256 verified.
[Archive manifest](data/rtx4090-inclusive-2026-09-15/artifact-bundle-manifest.json) ·
[archive identity](data/rtx4090-inclusive-2026-09-15/artifact-bundle.json).

The native archival executable embeds the original checkout asset path; rebuild
it when using another checkout. The branch does not commit generated executables
or the uncompressed 1 GiB input. They are provided in the local evidence archive.

Full command-by-command reproduction is in
[SCAN_INCLUSIVE.md](../integration/SCAN_INCLUSIVE.md). On a checkout containing
the delivery wrapper, the same declared sequence can be invoked with:

```powershell
& tools/Invoke-InclusiveScanReproduction.ps1 -Repository 'path/to/checkout' -DotNet 'path/to/dotnet.exe'
```

The wrapper creates a new evidence directory, holds the shared lock, preserves
failed attempts, and performs build, CPU/compiler/GPU gates, discovery, freeze,
18-process confirmation and analysis. Use .NET 10.0.302 and supply another
exact adapter name / MSBuild path if needed. The wrapper itself received syntax
validation; its individual commands were executed in the recorded run. CPU-only
reanalysis of the checked-in raw logs requires no GPU or native executable:

```powershell
python tools/run_inclusive_scan.py analyze --confirmation docs/results/data/rtx4090-inclusive-2026-09-15/confirmation-01 --output .scratch/reanalysis.json
```

## English résumé candidate (not applied to any résumé)

> Extended a GPUPrefixSums-derived HLSL scan with native inclusive output and a reusable D3D12 SDK API, reducing complete GPU scan time by 19% versus GPUPrefixSums RTS on RTX 4090 for 2^28 uint32 elements, confirmed across 18 fresh processes.

Keep the named hardware, workload and upstream attribution when using this
sentence. The result concerns complete GPU operation time, with application
end-to-end latency and cross-GPU generality unmeasured.
