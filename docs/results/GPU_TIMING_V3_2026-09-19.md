# GPU timing v3: native timestamp identity validated in short diagnostics

The zero-Recorder diagnosis is narrower now: Unity can capture positive GPU hierarchy timings on this RTX 4090 in both D3D11 and D3D12. Prelaunch Editor GPU profiling plus Autoconnect also restores nonzero legacy Recorder samples, but fixed three-frame attribution is incomplete. A native D3D12 timestamp plugin now resolves all 96 explicitly identified submissions in both normal and batchmode short diagnostics, with profiler capture disabled. This is **measurement availability/identity evidence, not a CPU/GPU performance comparison**. PR #12 stays Draft; formal timing and merge remain blocked.

## Controlled probes and findings

Graphics Jobs is now explicitly false in the build script. Both APIs are compiled into the same diagnostic binary, with D3D12 first/default and D3D11 only a command-line diagnostic override. Normal standalone reports `MultiThreaded`; batchmode reports `SingleThreaded`. These launch modes therefore change threading as well as process launch behavior. Both are Jobs-off modes; the absence of a build-script assignment in v2 did not prove Jobs had been enabled.

The [Unity GPU profiler documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/ProfilerGPU.html) documents the Graphics Jobs restriction. [Startup profiler arguments](https://docs.unity3d.com/6000.0/Documentation/Manual/profiler-command-line-arguments.html) were used to collect raw files before script execution. These are availability probes on an active desktop; load snapshots are retained for scripted standalone probes. The Autoconnect experiment ran with an Editor and is not quiet performance evidence.

| Configuration | Recorder Range/Cull/Draw nonzero observations | Independent raw GPU hierarchy |
|---|---|---|
| Graphics Jobs off, D3D12, normal and batch | 0 / 0 / 0 in each | No capture requested |
| Jobs off, startup profiler + GPU area enabled in script, D3D12 | 0 / 0 / 0 in each | Batch raw: 95 positive entries for each marker |
| Same binary/settings, startup profiler, D3D11 | 0 / 0 / 0 in each | Batch raw: 95 positive entries for each marker |
| D3D12 Autoconnect build, Editor GPU area verified true before launch | 96 / 96 / 96 | Saved Editor raw extraction has zero positive GPU entries; not a verified GPU-window capture |

For the startup probes, `Profiler.enabled` was true at diagnostic initialization while GPU area was false before the script call. Thus these runs **still do not establish GPU-module activation before driver initialization**. They do establish positive GPU capture capability despite zero Recorder values. Raw hierarchy extraction uses Unity's GPU-time column, not CPU sample time; the D3D12 batch capture includes e.g. Cull 0.009 ms / Draw 0.022 ms. These example diagnostic values are not benchmark stage estimates.

The Autoconnect controller opens the profiler through Editor APIs where available, sets GPU area before launching, and saves the Editor capture. A setup receipt records `gpuAreaBeforeLaunch: true`; the Editor log confirms connection to the Player. Player initialization nevertheless reported GPU area false, and the saved Editor raw did not contain positive GPU hierarchy. We do not claim the strict GUI/module setup was independently verified or that it isolates one activation setting. Its Recorder observations are independently positive. Their pulse-code matches at lag 3 were only **68/96**, with 55/96 at lag 4. Nonzero timing is insufficient to authorize guessed frame attribution.

Consequently, neither D3D12 profiling being generally unsupported nor a permanent Recorder API bug is established. Activation/connection state affects the Recorder path, and that path still fails the explicit attribution gate. There is no reason here to move the formal experiment to D3D11.

## Native D3D12 implementation and proof

The plugin compiles against the **installed Unity 6000.3.13f1 PluginAPI headers**, whose hashes are in the build receipt. No upstream headers are copied into the repository. It uses `IUnityGraphicsD3D12v7` and two event configurations:

- A one-time queue-access event on Unity's submission thread obtains graphics queue timestamp frequency and creates the query heap/readback resources.
- Recording events have queue access disabled, no flush or worker-sync flags, and use `CommandRecordingState` on Unity's recording thread. They do not access or submit to the command queue.

T0 precedes reset/cull, T1 follows cull, and T2 follows count-copy and indirect draw. Queries use `EndQuery` and `ResolveQueryData`. The 32-slot ring retains explicit monotonically increasing IDs and expected stage order; IDs 33–96 exercise slot reuse. A slot becomes reusable only after the captured Unity frame fence has completed and its result has been consumed. The read path never waits, signals a fence, flushes, or submits a command list. Native error, unavailable interface/frequency, busy slot, wrong generation/stage, device removal, unordered timestamps or incomplete drain fail the diagnostic.

[Microsoft's timing contract](https://learn.microsoft.com/en-us/windows/win32/direct3d12/timing) specifies queue-frequency conversion. Milliseconds are `(T1-T0)*1000/frequency`, `(T2-T1)*1000/frequency`, and `(T2-T0)*1000/frequency`. Raw integer ticks, required/completed fence values, submission/availability Unity frames and DLL/source hashes are retained. The implementation follows the [Unity native rendering interface](https://github.com/Unity-Technologies/NativeRenderingPlugin) and its installed header's distinct queue-versus-recording-thread access rules.

| Final native diagnostic | Result | Queue frequency | Observed availability delay | Threading | Profiler enabled |
|---|---|---|---|---|---|
| Normal standalone | 96/96 unique IDs, positive ordered timestamps, completed fences | 1,000,000,000 Hz | 1–7 Unity frames | MultiThreaded | false |
| Batchmode | 96/96 unique IDs, positive ordered timestamps, completed fences | 1,000,000,000 Hz | 1–5 Unity frames | SingleThreaded | false |

The initial plugin build also passed 96/96 in each mode (delays 1–5 and 1–6); both initial and final runs are preserved. A transient Python checker incorrectly required MultiThreaded for batchmode after the final process completed. It was corrected to accept both non-Jobs modes and the existing raw result was reassessed; no hardware run was discarded or silently retried.

Native cull in this diagnostic can repeat dispatch 1–4 times deterministically before one draw to exercise varying work. This is not the measured benchmark workload. Count-copy is in the native draw interval, unlike v2 where it was in cull; those substage values must not be pooled. Plugin event/query overhead and native instrumentation's impact on scheduling remain unquantified.

## Current acceptance boundary

The native mechanism is validated **only in the dedicated GPU diagnostic**, not yet integrated into the timed CPU/GPU runner or promoted to a primary comparable metric. The existing legacy timing gate still rejects the ambiguous Autoconnect data; `measure` remains disabled. No CPU/GPU pilot, three-pair study or formal matrix was run in this revision. Batch-completion/fence quantization and quiet steady-state pacing for the actual benchmark remain unvalidated. The native results do not validate v2's separate batch-completion metric.

Final Player source identity: `1d8fdd893205a289cab222471e5352a7949184f1159bae05b5c19bebebf7997f`. Final build receipt records Jobs off and Autoconnect off. The same final binary passed the existing 100k/25% correctness cell: **10/10 exact sets/counts and non-black RGBA image comparisons**. Remaining 17 correctness cells are not claimed passed.

Local checks: Unity final standalone build succeeded; native MSVC `/O2 /W4 /WX` compile succeeded; managed Release build succeeded; **221 C# tests and 29 Python CI-suite tests passed (including 9 crossover tests)**. New tests reject stale/reused IDs, incomplete fences, malformed timestamp order/frequency/units, profiler-enabled native results, Jobs modes and missing submissions. The hosted CPU/compile CI result and exact current commit are recorded in [Draft PR #12](https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/pull/12). CI cannot validate the installed Unity native headers or GPU hardware.

[Protocol v3](../../unity/GpuDrivenCrowdBenchmark/PROTOCOL_V3.md) and [evidence manifest](../evidence/gpu-timing-v3-20260919/manifest.json) preserve 42 JSON/CSV artifacts, hashes and local raw-capture paths/hashes. Large raw profiles and binaries remain local; three extracted hierarchy CSVs are committed. v1/v2 evidence is unchanged. Native source and DLL identities are separate, so an unchanged managed assembly cannot hide a plugin change.
