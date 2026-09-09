# Primitive integration and positioning — 2026-09-10

Base: `ca941ebbd0e9302797a3191346ad56f7323515cc` on the existing
`codex/repair-20260908-hlsl-integration` branch in the `hlsl-integration` worktree.
This change is a local integration/documentation commit. The direction is
reusable GPU primitive execution, trustworthy measurement and mature backend
choice; internal algorithms remain explicit research candidates.

## Existing functionality reviewed first

`PrimitiveOperations.ExclusiveScan` already exposed
`ScanImplementation.GpuPrefixSumsReduceThenScan` and `StableSort` already exposed
`SortImplementation.AmdParallelSort`. Both accept application input; sort handles
full-width keys and stable arbitrary uint32 payloads. The existing selector checks
the requested/fallback contract, shader-model/wave support, pinned source and exact
deployment identity. The existing `D3D12OperationRecorder` borrows application
buffers, PSOs, root signature and state map without tuning or owning submission.

This change reuses those APIs. It adds no backend, shader algorithm, default
promotion, new deployment-profile format or automatic winner rule. Internal scan
and binary-sort defaults remain compatibility baselines, with no general speed
claim. The pinned upstream RTS and AMD algorithms remain attributed to their
original authors.

## Real changes

- [Application example](../../examples/HlslPerf.PrimitiveApp/README.md): a normal
  executable builds RTS exclusive offsets from visible-item counts and AMD stable
  material ordering from keys/draw IDs. It opts in explicitly, reports the selected
  plan/identity/status, and runs 12 small CPU plan checks. Its runtime identity is
  labeled as a CPU fixture, not a detected GPU/compiler or historic deployment.
- The compiled `ApplicationRecording.RecordAndSubmit` uses the real public
  borrowed-resource recorder and Vortice queue/fence API. The application supplies
  initialized resources, exact PSOs, state, an open list and monotonic fence
  values. It retains ownership through completion/recovery; `Main` never invokes
  this GPU path. The example documents upload/immutable input, state resync/decay,
  repeated use, consumer ordering and device-removal boundaries.
- Complete costs are visible: CPU plan/oracle/hash work, compilation/allocation,
  upload, restore/deinterleave, scratch initialization, all algorithm passes,
  trim/conversion, barriers, CPU submission/fence wait, consumption and validation.
  Logical byte counts explicitly exclude allocation alignment and other overhead.
- A real selection defect was reproduced: `InvalidDataException` does **not**
  derive from `IOException`, so a pinned revision/byte mismatch escaped the old
  catch. Truncated lock JSON also escaped as `JsonException`. `Select` now catches
  those two data-error categories and returns the already validated baseline with
  a diagnostic. Invalid input/fallback contracts still throw. No source check is
  bypassed to permit an external implementation.
- Twelve isolated source-fault regressions cover revision, bytes, missing file,
  JSON syntax and eight structural failures: missing `sources`, non-array
  `sources`, absent/duplicate source entry, missing `files`, non-array `files`,
  empty `files` and a null path. They use nonempty scan inputs and temporary
  copies of the small kernel tree; they never mutate the pinned checkout or old
  caches. `VerifyPinnedSource` normalizes malformed lock fields to
  `InvalidDataException` and rejects empty file lists. This normalization stays
  inside source verification; caller root validation and invalid input/fallback
  contracts retain their errors.
- README, SDK first use, roadmap and the portfolio entry now foreground application
  integration, measurement boundaries and mature backend reuse. The latest result
  link points to September 9; September 8 preparation status and September 7
  figures are explicitly historical. The new example is in the solution and in
  the existing CPU CI allowlist. No CI workflow was triggered in this work.

## Evidence interpretation retained

The [September 9 native report](../results/R9700_NATIVE_CONFIRMATION_2026-09-09.md)
contains seven real comparisons and all 70 timing processes. Only tile4 beats
GPUSorting's vendored FFX on its `2^25` pair workload: 12.124 ms versus 15.038 ms,
baseline/candidate ratio 1.2404, reported 95% interval 1.2400–1.2408. The other six
candidate comparisons are slower. These findings support targeted investigation
and reuse; the timings alone do not identify a hardware-counter cause.

DeviceRadixSort and OneSweep remain native-harness backends, not public
`PrimitiveOperations` options. GPUSorting's vendored FFX is distinct from the
FidelityFX SDK 1.1.4 shader adapter used by this example. None of the seven native
results is an SDK/Unity deployment profile, and the new source inherits no win
rate. No old report, JSON, CSV, plot, kernel, native source, source lock or Unity
consumer asset was rewritten.

## Actual local validation

All commands below ran on the CPU with .NET SDK `10.0.302` or Python. No dependency
download was needed. The new project's restore used a configuration with **zero
package sources**, `--no-dependencies` and `NuGetAudit=false` to reuse cached
packages and existing project assets.

| Check | Result |
|---|---|
| New source-fault tests against the original Workloads binary | 3 failures reproduced (revision, bytes, JSON); missing-file case already passed |
| Workloads single-project incremental Release build, `--no-restore -p:BuildProjectReferences=false` | Passed, 0 warnings / 0 errors |
| Example single-project Release compile/link against existing dependencies, `--no-restore -p:BuildProjectReferences=false -p:CopyLocalLockFileAssemblies=false` | Passed, 0 warnings / 0 errors, including the D3D12/Vortice method signatures |
| Example CPU entry point with `--check` | 12 checks passed; RTS and AMD selected explicitly, both `Unmeasured`; no device or GPU executor called |
| Initial `PrimitiveOperationTests`, incremental build against updated Workloads | 8 passed, 0 failed, 0 skipped; four existing contract/profile tests plus four source-fault cases |
| Eight added structural fault cases against the initial integration binary | All eight reproduced failures: unexpected parsing exceptions or an accepted empty file list |
| Final `PrimitiveOperationTests` after structural validation repair | 16 passed, 0 failed, 0 skipped; four contract/profile tests plus twelve source-fault cases |
| Final incremental Workloads/example builds and example CPU entry point | Both builds passed with 0 warnings / 0 errors; all 12 example checks passed against the repaired binary |
| `python tools/verify_external_sources.py` | Passed all 114 pinned files; lock SHA-256 `8d707858e69288fa6f10c9ec208d340cce3fa73b21d0240cbd3607d9b1120334` |
| `python tools/check_source_checkout.py` | Passed existing tracked checkout; long-path setting remains enabled |
| Local Markdown link/anchor check | 48 targets across six changed documents passed |
| Solution/project/config XML check | All 12 solution project paths and both example references exist; offline configuration contains no feeds |
| Historical preservation check | No diff from the base in results/evidence, kernels, third-party/native sources, Unity assets or original figure/data/renderer; both native runtime executable hashes match September 9 evidence |
| `git diff --check` | Passed before committing; staged additions checked as well |

The first example compilation exposed two sample-only mistakes: accessing the
internal `WorkloadData` helper and treating Vortice's void `Close()` as a result.
They were corrected to BCL byte conversion and the actual public `Close()` call;
the SDK was not expanded to accommodate sample code. The final compile passed.

The final structural-fault red/green run and incremental build/example logs are
retained locally under `.scratch/positioning-shape-20260910/` as `before-fix.log`,
`after-fix.log`, `workloads-build.log`, `example-build.log` and `example.log`.
These are CPU regression checks and add no GPU execution or performance evidence.

Commands for the passing focused checks, from the repository root:

```powershell
dotnet restore examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj --configfile examples/HlslPerf.PrimitiveApp/NuGet.offline.config --no-dependencies -p:NuGetAudit=false
dotnet build src/HlslPerf.Workloads/HlslPerf.Workloads.csproj -c Release --no-restore -p:BuildProjectReferences=false
dotnet build examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj -c Release --no-restore -p:BuildProjectReferences=false -p:CopyLocalLockFileAssemblies=false
dotnet run --project examples/HlslPerf.PrimitiveApp -c Release --no-build --no-restore -- . --check
dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --no-restore -p:BuildProjectReferences=false --filter FullyQualifiedName~PrimitiveOperationTests --logger 'console;verbosity=minimal'
python tools/verify_external_sources.py
python tools/check_source_checkout.py
```

These incremental commands require current prebuilt dependency outputs. The
example's normal build instructions explain the fresh-project distinction.

## Unverified scope and resource limits

There was no Unity import, Player run, GPU correctness/performance execution,
shader/DXC run, native compile/link, driver/counter probe, full solution rebuild,
package creation, dependency download or benchmark queue resumption. Managed
compilation verifies public API references; it does not validate native loading,
recorded GPU resource transitions, PSO provenance, queue execution, fence recovery
or end-to-end application costs. CPU oracle metadata is not a GPU output result.

The application must supply real runtime identity, compile the declared shaders,
allocate/upload correctly and validate actual execution. Pair-sort fallback also
requires wave64; an unsupported device does not magically get a CPU fallback.
Operation profiles bind exact input and oracle bytes as well as source/runtime,
so a changing stream cannot reuse one input's confirmation as a generic policy.

The example's `bin` + `obj` outputs totaled 1,166,739 bytes at validation, avoiding
native compiler copies. No old cache was deleted; only each source-fault test's
own newly created temporary directory was disposed. `.scratch/runtime-20260909`,
`.scratch/native-runtime-01`, native build directories and native package caches
remain in place. The historical figure source and images remain unchanged.

No remote push, task/subagent creation, desktop task-management API, message send,
other-repository work or subsequent performance action is part of this delivery.
