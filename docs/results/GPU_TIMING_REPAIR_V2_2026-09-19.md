# GPU timing repair v2 — second STOP, 2026-09-19

PR #12 remains Draft and is **not infrastructure-merge-ready**. Six bounded availability probes returned zero GPU samples. No v2 CPU/GPU timing pilot, three-pair pilot, formal matrix, crossover estimate or performance claim was produced. The previously RenderDoc-validated rendering architecture was not revalidated or changed. This remains separate from the September 16 CPU-output complete-task workload.

## Diagnosis and API selection

The v1 unique-per-frame sampler design did not establish delayed attribution. v2 replaces it with three stable markers and a deterministic nonperiodic repetition code. However, stable markers alone did not restore GPU samples. Legacy CustomSampler/Recorder and modern SampleGPU ProfilerMarker/GpuRecorder ProfilerRecorder both failed in normal standalone and batchmode. All marker/recorder validity flags and GPU-recorder support flags were true; these flags therefore do not prove available timings. Legacy CPU sample blocks were nonzero (maximum 8 normal, 4 batch), while GPU blocks and nanoseconds stayed zero.

The initial four probes reported the GPU profiler area disabled. A bounded follow-up explicitly enabled that area and verified the flag changed to true; both legacy launch modes still returned zero. That setting alone is insufficient. The strongest remaining diagnosis is an unresolved Unity GPU profiler/backend/configuration issue; the evidence does not isolate an exact root cause or prove the platform universally unsupported. No timing API or launch mode is selected. There is **no proof of resolving GPU samples**. Batchmode is not an established cause: both modes fail on the same binary in each A/B.

Hardware: RTX 4090, NVIDIA 591.86 (Player 32.0.15.9186), Core Ultra 7 265K, Windows 11, Unity 6000.3.13f1, D3D12, Development Mono without deep profiling or script debugging. Availability probes ran on an active desktop, retaining before/after load snapshots. They cannot serve as quiet performance pilots.

| Probe / GPU area | Launch | GPU nonzero observations, Range/Cull/Draw | p10 / median / p90 / p95 update ms | CV |
|---|---|---|---|---|
| Legacy / unchanged | Normal | 0 / 0 / 0 | .0595 / .1248 / .3558 / .5417 | 8.6245 |
| Legacy / unchanged | Batch | 0 / 0 / 0 | .0294 / .1386 / .2614 / .5535 | 3.3218 |
| Modern / unchanged | Normal | 0 / 0 / 0 | .0574 / .1235 / .3629 / .7214 | 8.3951 |
| Modern / unchanged | Batch | 0 / 0 / 0 | .0226 / .1214 / .2132 / .7456 | 3.7179 |
| Legacy / enabled | Normal | 0 / 0 / 0 | .0423 / .1178 / .2484 / .3952 | 8.7509 |
| Legacy / enabled | Batch | 0 / 0 / 0 | .0283 / .1133 / .1648 / .4594 | 3.7115 |

Each probe has 96 submission frames and 112 observation frames, including drain. Pacing summaries include diagnostic startup/drain and are not steady-state pilot statistics. No preregistered interval concentration was detected (maximum cluster fraction 1/111), but clean measured-workload pacing remains unvalidated. Raw assessments retain the exact statistics.

## Attribution and completion boundary

The [documented legacy GPU count delay](https://docs.unity3d.com/cn/6000.0/ScriptReference/Profiling.Recorder-gpuSampleBlockCount.html) is three frames. The implementation maps availability Unity frame minus three to the submitted Unity frame, verifies its measured index, and rejects missing/duplicate/ambiguous samples. The diagnostic compares a 1–4 repetition code at lags 1 through 8; only complete agreement at lag 3 is eligible. All observed matches were zero. Mapping is unit-tested, **not hardware-validated**. Modern GPU collection uses the documented [GpuRecorder option](https://docs.unity3d.com/cn/6000.0/ScriptReference/Unity.Profiling.ProfilerRecorderOptions.GpuRecorder.html); it did not provide a working alternative here.

Both timed arms now have the same proposed batch boundary: finish warmup behind a separate fence; timestamp first measured CPU work; submit the fixed sequence; place one final all-GPU-operations fence after the last draw; poll in later Updates without a blocking wait or further benchmark submissions. Raw fields preserve first-work, final-submission, last-false and first-true poll ticks and frequency. The enclosing observation gap divided by 1000 bounds the reported per-frame observation granularity. `batchCompletionMsPerFrame` is an amortized completion/throughput candidate, not individual-frame latency. This code built but was not exercised by a valid timing pilot: completion and observation granularity are **unavailable**, not zero. Correctness mode intentionally does not exercise these fences.

## Checkpoint status

| Gate | Result |
|---|---|
| Selected API and verified attribution | STOP: none of six probes resolved a GPU sample |
| One fresh CPU process, 300 + 1000 frames | Not launched in v2; no valid CPU performance result |
| One fresh GPU process, 300 + 1000 frames | Not launched; no valid GPU performance result |
| Three additional fresh pairs | Not reached; stability unavailable |
| Final-binary 100k / 25% correctness | Passed all 10 exact visible-set/count and non-black image checks |
| Remaining 17 correctness conditions | Deferred while timing remains unresolved; not 18/18 |
| Formal 216-process matrix | Not launched; runner explicitly disables `measure` at this checkpoint |
| Infrastructure merge readiness | No; PR remains Draft |

The final correctness run records driver 32.0.15.9186 and current source identity. Image equality includes the same six-vertex geometry, material and deterministic depth rule. No timing readback or CPU-driven GPU instance-count decision was introduced. The final refactored binary differs from the diagnostic binaries: earlier probe failures do not validate final timing code. Future resumption needs a new diagnostic on the final candidate, not reuse of a different source identity.

## Protocol, evidence and validation

[Protocol v2](../../unity/GpuDrivenCrowdBenchmark/PROTOCOL_V2.md) records stable-marker attribution, bounded same-binary launch A/B, the profiler-area follow-up, proposed completion boundary, and interval-based pacing tests across 30–360 Hz. All quality pilots require three <=5% GPU-load snapshots, retain blocked receipts, and never retry silently. Zero samples, ambiguity, synchronization, fence failure, pacing or drift stop progression. API availability probes are explicitly distinguished from quality pilots. The v1 protocol and [v1 stop evidence](GPU_RESIDENT_CROSSOVER_2026-09-19.md) remain unchanged.

[Raw v2 evidence and SHA-256 manifest](../evidence/gpu-timing-repair-v2-20260919/manifest.json) contain 23 byte-preserved JSON files: six raw probes, launch/load/binary receipts and assessments, final calibration/correctness and build receipt. Local binaries and Player/editor logs are excluded. Raw Unity source identities:

- Initial stable-marker A/Bs: `6b14c7352e9512bbdedb6a72f93394f8ed8f8f18584281b51f2c23b78eb5b85e`.
- Explicit GPU-area A/B: `598b051ecbc0c260e736fe2684356ba77a58d89f4660e55707ad5fc6fe17671c`.
- Final build/correctness: `fcc468fd40e944d6a3df96b31910be9a84617e96758ef1b6389b94d2bcf32de3`.

Local validation: standalone Unity build succeeded; managed Release build passed with zero warnings/errors; **220 C# tests and 28 Python CI-suite tests passed**. New tests cover frame-delay boundaries and pulse codes, diagnostic API parsing, zero/wrong-delay rejection, multiple refresh-rate pacing clusters, synthetic batch boundary/attribution failures, and blocked pilot/pilot-pairs receipts without starting a Player. Synthetic tests do not supply hardware performance evidence. Hosted CI status and the exact reviewed head are tracked in [Draft PR #12](https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/pull/12); CI is CPU/compile validation, not GPU timing acceptance.
