# HLSL vNext integration — R9700, 7 September 2026

The four requested implementation streams are integrated and the bounded native,
Unity, paired measurement and RGA gates have run. The 16,777,216-element scan
confirmed single-pass speedups of **1.4782x** with one resident input and **1.5254x**
with three rotating resident inputs. Wide radix and dynamic configurations did
not establish a deployable performance advantage. Their rejected measurements
remain in the evidence; no production default was changed.

Formal measurements used clean source
`9bde8741c9f9a7e51910f3c6ded47bf69e1e081c`. Subsequent integration changes contain
documentation and evidence only. The final checkout/merge receipt identifies the
delivery commit separately from this measured source.

## Implemented contracts

| Area | Delivered behavior and compatibility |
|---|---|
| Paired measurement | Reproducibly shuffled ABBA/BAAB blocks; block/position/order/input provenance; paired log-ratio Student-t confidence intervals; baseline drift, CV, p95 and minimum benefit guards; selection locked before fresh-seed independent confirmation. |
| Checkpoints and profiles | Paired checkpoint v2 archives interrupted attempts and starts a fresh session; completed replay does not resample. Historical checkpoint v1 and profile v2 remain explicitly historical. Profile schema 3 binds device/driver, workload implementation, ABI, execution, confirmation, candidate and canonical define hash. Unity requires project-owned `HlslPerfDeploymentIdentity`; incoming self-attestation is insufficient. |
| Dynamic ABI | `KernelAbiV2`, `KernelPassSpec.DependsOn/Indirect`, `KernelIndirectDispatch`, `AdditionalVerifiedOutputs`, `GetVerifiedOutputs()`, per-resource/per-attempt `CorrectnessResult.Outputs`, and `D3D12Tuner.ValidateExecutionPlan`. GPU reset/count, bounded argument construction, transitions and indirect consumption execute inside the complete plan. ABI v1 remains supported. |
| Stable sorting | Real stable 1/4/8-bit uint radix plans, interleaved key/payload input and separately verified keys/payloads, independent comparison-sort oracle, duplicate-key original-index stability, full uint and partial-digit domains, zero/one/partial/large counts. `RadixSortContract.Describe` exposes digit/pass/allocation/scratch costs. |
| Working sets and analysis | `WorkloadScenario` and disposable `ScenarioSession` retain real independently seeded buffers; `MeasureBatch`, `VerifyAll` and memory snapshots expose actual input/output hashes and allocation scopes. RGA analysis retains each invocation, failed/partial status, source/DXIL/tool/compiler identity and raw ISA/statistics. |

Public manifest schema 3 permits zero fixed defines (for example keys-only
`HLSLPERF_RADIX_PAIRS=0`) while candidate axes retain positive-value rules.
ABI v2 permits zero logical work; historical v1 retains its positive-count rules.
Unity's execution consumer remains ABI v1-only; ABI v2 is available through the
native generic executor, not advertised as supported by that Unity consumer.

## Verification actually executed

| Gate | Integrated result |
|---|---|
| Release solution | Build succeeded with zero warnings/errors. |
| Core tests | Final combined code: 91 passed, zero failed/skipped. |
| Unity 6000.5.3f1 | Actual profile-consumer EditMode run: 7 passed, zero failed. |
| Dynamic native | 11 positive ABI v2 cases, 110 output/poison checks; legacy v1 adds 2 checks. Two deliberately broken cases were rejected, preserving four expected failed hashes. |
| Radix native | 144 candidate executions; all 420 output/poison checks passed, including pair stability, edge dimensions and v1 compatibility. |
| Paired/resume native | Two independently sampled 48-observation sessions with three resident slots; 576 positive checks. Completed replay reused the original session; five interrupted observations were archived before a fresh full session. Both short noisy sessions correctly withheld deployment. |
| Working-set/RGA native | Five cells, 52 output/poison checks; four real RGA entrypoint invocations. |
| Formal matrix | 18 complete reports, 3,936 observations, 19,584 output/poison checks; every compiled candidate and every expected output passed. |
| Independent acceptance audit | Replayed geometric ratios and conservative Student-t intervals, drift and CV; checked phase-separated actual inputs, every declared resource/two poison values, selection lock, allocation limits and publication decisions. All 18 manifests, 18 checkpoints and three profiles passed their JSON schemas; 19 frozen runtime file hashes matched. |

The final Core run and native suite used source `8827f934082ed80d2d4bdd8b53e33d8e71ea9bbe`;
the last pre-formal change was the independently validated manifest schema fix.
Unity's package code was unchanged after its integrated run at
`fd8a35f0308f74ac95bd3d5c6eacb9387f67f36d`.

## Formal design and results

The acceptance declaration preceded GPU sampling. Each phase used eight blocks,
CV <= 0.05, baseline drift <= 0.15, a 95% paired interval and minimum speedup 1.01.
Calibration selected one candidate; independent confirmation used fresh declared
input seeds and timing samples. A rejected challenger did not trigger selection
of a different candidate from confirmation data. No gates were relaxed.

The six binary sorting screening cells evaluated 90 candidate summaries. A binary
control was chosen by the lowest stable, correct calibration median from the
in-repository candidate space, with a deterministic ID tie-break. Five controls
qualified for separately declared 1/4/8-bit comparisons. The 65,537-key single-slot
screen had no stable binary control, so its derived comparison is explicitly
unavailable under the predeclared rule. This is not a passed performance comparison.
All six screening reports are retained, including their rejected confirmation.

| Workload/scenario | Confirmation speedup, 95% interval | Decision |
|---|---:|---|
| dynamic-n4096-active0 | 1.0274x [0.9808, 1.0763] | 64-thread indirect self-control; noisy, no profile |
| dynamic-n4096-active1 | 1.0066x [0.9372, 1.0812] | 64-thread indirect self-control; noisy, no profile |
| dynamic-n4096-active2 | 0.9794x [0.9496, 1.0102] | 64-thread indirect self-control; noisy, no profile |
| radix-compare-keys-n262144-slots1 | 1.0160x [0.9288, 1.1115] | Binary self-control retained; noisy, no profile |
| radix-compare-keys-n262144-slots3 | 0.9723x [0.9088, 1.0403] | Binary self-control retained; noisy, no profile |
| radix-compare-pairs-n262144-slots1 | 1.0141x [0.9467, 1.0862] | Binary self-control retained; noisy, no profile |
| radix-compare-pairs-n262144-slots3 | 1.0296x [0.9430, 1.1241] | Binary self-control retained; noisy, no profile |
| radix-compare-pairs-n65537-slots1 | 1.0243x [0.8460, 1.2402] | Binary self-control retained; noisy, no profile |
| scan-n16777216-slots1 | 1.4782x [1.4734, 1.4830] | Single-pass selected; profile eligible |
| scan-n16777216-slots3 | 1.5254x [1.5149, 1.5360] | Single-pass selected; profile eligible |
| scan-n4194304-slots1 | 1.0044x [0.9950, 1.0139] | Existing baseline retained; no new gain |
| scan-n4194304-slots3 | 0.9976x [0.9913, 1.0039] | Baseline confirmation passed; calibration CV rejected profile |

The three eligible profiles are evidence artifacts only: two 16M scan improvements
and one 4M retained baseline. They were not installed or promoted to defaults.
The dynamic matrix compares group sizes 1 and 64 within the same indirect demo at
zero, predicate-selected and all-active populations. It validates complete-plan
measurement and rejection behavior; it does not measure direct-versus-indirect
dispatch overhead. Fixed-dispatch compatibility is covered by the separate v1
native control. The serial compaction demo is an ABI demonstration, not a tuned
replacement for the optimized compaction workload.

### Complete radix plan cost

The following medians are **calibration descriptions**, not independently confirmed
benefits. Some 8-bit ratios looked promising, but every wide challenger failed the
full selection guards. No wide-radix speedup claim is supported. All use 32 key
bits; digit iterations are respectively 32, 8 and 4. Dispatch counts include scan
hierarchy passes and the pair-output split. Scratch is the documented sum of
uninitialized, non-verified logical buffers, including alternate record storage;
it is not hardware local-memory scratch or WDDM allocation size.

| Cell | Bits/digit | Median complete plan, ms | Dispatches | Logical bytes/slot | Scratch bytes/slot |
|---|---:|---:|---:|---:|---:|
| keys-n262144-slots1 | 1 | 0.325546 | 160 | 5,244,932 | 3,147,780 |
| keys-n262144-slots1 | 4 | 0.545537 | 40 | 3,277,316 | 1,180,164 |
| keys-n262144-slots1 | 8 | 0.303947 | 28 | 5,251,108 | 3,153,956 |
| keys-n262144-slots3 | 1 | 0.474959 | 160 | 5,246,980 | 3,149,828 |
| keys-n262144-slots3 | 4 | 0.532711 | 40 | 3,277,316 | 1,180,164 |
| keys-n262144-slots3 | 8 | 0.289866 | 28 | 5,251,108 | 3,153,956 |
| pairs-n262144-slots1 | 1 | 0.358971 | 161 | 10,489,860 | 6,295,556 |
| pairs-n262144-slots1 | 4 | 0.513319 | 41 | 8,520,196 | 4,325,892 |
| pairs-n262144-slots1 | 8 | 0.249021 | 29 | 10,493,988 | 6,299,684 |
| pairs-n262144-slots3 | 1 | 0.500343 | 161 | 10,489,860 | 6,295,556 |
| pairs-n262144-slots3 | 4 | 0.529524 | 41 | 8,520,196 | 4,325,892 |
| pairs-n262144-slots3 | 8 | 0.284161 | 29 | 10,493,988 | 6,299,684 |
| pairs-n65537-slots1 | 1 | 0.214914 | 161 | 2,622,004 | 1,573,412 |
| pairs-n65537-slots1 | 4 | 0.332331 | 41 | 2,130,220 | 1,081,628 |
| pairs-n65537-slots1 | 8 | 0.201003 | 29 | 2,625,596 | 1,577,004 |

The declared wide configurations were group 128, two items/thread, scalar loads,
wave backend 2, wave size 32. This was a bounded comparison, not an exhaustive
wide-radix optimum. Exact control defines, candidate IDs, all timings and rejected
calibration intervals are in the acceptance JSON and raw reports.

## Hardware and measurement limits

Device: AMD Radeon AI PRO R9700, driver `32.0.31041.1004`, Windows build 26200,
D3D12. Native DXC `1.9.2602.17`, Vortice.Dxc `3.8.3.0`, .NET SDK `10.0.302`.
The compiler DLL SHA-256 is
`b86a738ece4c05dbe2d9bbb29668a2ccb28a0740773c9b027cdc61e8d07b4d75`;
the formal D3D12 assembly is
`5b07768b396ee3508dcb824e686a17cc5393726eb52df427d8f75160cccb5a7c`.
The declaration records all 19 runtime identities and raw observations record
actual loaded native modules and per-entry DXIL hashes.

RGA 2.14.2.7 performed DX12 live-driver analysis for gfx1201 with its separately
attested DXC 1.8.2502.8. Static results below do not substitute for measured
occupancy, bandwidth or runtime counters.

| Entry point | VGPR used | SGPR used | LDS bytes | Reported scratch bytes |
|---|---:|---:|---:|---:|
| ResetSinglePassState (each of two plans) | 3 | 14 | 0 | 0 |
| SinglePassScan | 45 | 26 | 1,056 | 0 |
| FusedCompactSinglePass | 44 | 58 | 1,056 | 0 |

VGPR/SGPR spills, measured occupancy and measured bandwidth are unavailable/null;
they are not inferred to be zero from static scratch. Raw stdout, stderr, exit
codes, arguments, ISA, live-register and statistics files are retained. This
bounded RGA run analyzes the specified scan/compaction configurations; it does
not assert that every formal candidate has identical resource usage.

GPU timestamps enclose the complete repeated execution plan, including reset,
count/argument preparation and resource transitions. Upload, PSO creation,
post-timing poison/re-execution, readback and host oracle work are excluded.
All builds, Unity and GPU runs used the shared cooperating-task mutex. The largest
observed formal committed allocation was 403,832,832 bytes per arm (385.125 MiB),
below the 512 MiB cap; two arms stay below the declared 1 GiB comparison budget.
Three-slot rotation changes real inputs but is not guaranteed cache-cold or
physically pinned residency. DXGI budget/usage is not proof of per-resource residency.

Temperature and clocks were neither sampled nor controlled. Browser, desktop and
other external application interference remained possible and process snapshots
are retained. Results concern this device, driver, declared kernels, dimensions,
seeds and single bounded session; they are not cross-device or repeated-day claims.
The parent reported an earlier unrelated import at 04:58:04–04:58:32 UTC; all
integrated native and formal GPU measurements here occurred after that interval.

## Preserved failures and provenance

* The first radix worker smoke preserved strict-DXC failures for 92 wide candidates;
  52 binary candidates were correct. A scoped loop-variable fix preceded the
  successful 144-candidate integrated run. Both worker attempts are archived.
* The first Unity regression wrapper did not complete after Unity itself passed.
  Its owned wrapper was stopped only after process identity and absence of live
  descendants were checked. The runner was corrected to await its Unity process
  and owned descendants without the inherited Windows job wait; the second full
  wrapper completed successfully. The first receipt remains incomplete.
* Formal matrix attempt 01 was rejected by the manifest schema before any GPU run
  because a valid zero-valued fixed define was disallowed. The public schema was
  corrected and tested without weakening positive candidate axes; attempt 02 is
  the only executed formal matrix. The initial declaration and rejection remain.
* No noisy or failed performance sample was removed, replaced with zero or silently
  reclassified as historical success. No user application was stopped and no global
  caches were cleared. No source/assets from employer projects or optional external
  sorting implementations were imported.

## Evidence and replay

The output directory contains `HLSL-vNext-acceptance.json`, this report,
`HLSL-vNext-evidence-index.json`, `SHA256SUMS.txt` and
`HLSL-vNext-raw-evidence.zip`. The archive retains formal declarations/manifests,
raw reports/checkpoints/CSVs/profiles, native successes and negative tests,
test logs/TRX/XML, failed attempts, worker evidence, replay scripts, the frozen
formal runtime and actual native/RGA compiler executables. Unity generated Library
caches are excluded; its execution receipt and complete logs/results are included.
Every archived file was read back and checked against its SHA-256 manifest.


Archive: 1,470 evidence files; 53,751,484 compressed bytes.
SHA-256: `28c1e9d1ec01dbb09140c648f511a2d400d2ed76eff65a4b2b1fa9ead5c5dd3c`.


From the repository, follow `docs/integration/REPLAY.md` for the serialized Release,
Unity and bounded formal matrix commands. Use a fresh evidence root for every
replay; never overwrite a prior attempt. The declared runner records new binary
and source identities. To compare a later implementation, retain both declared
runs and apply the same work, scenario and protocol; the historical timing tables
elsewhere in this repository were not retroactively upgraded to this protocol.

Worker final source tips: measurement `7c68649f753f8dd9ad59de65c922cf6dc5249021`,
dynamic `50f16c8b4c92bd2f76b2f2f8c57fd35384245057`, evidence
`61d9871680d953e38533438ba394bc31c11cc2a4`; radix changes through
`392f6815748aaf2524b578b31576965e7cb3ebb6` were cherry-picked (integration tip
`fd8a35f` for its evidence). Original source baseline was
`052cfdb033fdcf1f365514403737fdc584609dc7` on `codex/main`.
The delivery receipt records the guarded local fast-forward separately. No push
or release publication is part of this handoff.
