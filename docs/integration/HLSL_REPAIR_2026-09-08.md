# HLSL integration repair, 2026-09-08

The Scan and Sort implementations are integrated with the SDK, native upstream
adapters, Unity asset export, source packaging and PR validation. Tested code
commit: `93a913575b141f9afd793f654956758edbf0123d`, on
`codex/repair-20260908-hlsl-integration`. The following delivery commit adds only
this handoff and the [nonperformance receipt](../evidence/hlsl-integration-nonperformance-2026-09-08.json).

All new candidates remain explicit opt-ins and **Unmeasured**. This repair ran
builds, shader compilation, static inspection and deterministic CPU correctness
tests. It ran no GPU dispatch, benchmark, profiling/counters, calibration,
autotuning, Unity import or Player. Nothing was pushed, released or scheduled.
Normal SDK/CLI runtime entry points remain available. CPU CI selects reviewed
commands explicitly; the product contains no added chat-authorization gate.

| Gap | Delivered change | Evidence and remaining boundary |
|---|---|---|
| Existing local vNext work | Retained dynamic ABI, paired confirmation, source identities, scalar/uint4 IO, wide radix and prior measurement/evidence work. | Baseline is fetched `origin/main` at `9bba9fefb0c579052d4dfee429531c4145e9e4be`. Six reviewed branch tips are ancestors. The separate radix tip has six patch-equivalent commits already represented in the baseline; commit identity alone had obscured that. Exact audit is in the receipt. |
| Scan/compaction algorithm | Integrated wave-tiled uint4 access, parallel wave spine, bounded polling with private full-width fallback, and per-invocation state reset. | [Scan contract](SCAN_WAVE_TILED.md), CPU models and DXC checks. Full uint32 modulo semantics are preserved; GPU ordering/progress and performance remain unvalidated. |
| Stable radix algorithm | Integrated 4/8-bit tiled stable rank/reordering, bin-major histogram prefixes, reusable scratch and direct final SoA scatter. | [Radix contract](RADIX_REPAIR_HANDOFF.md), CPU tests, 74 shader compilations and static LDS inspection. No default promotion. |
| Native external workload boundary | Complete pinned GPUPrefixSums/GPUSorting D3D12 trees with local subclasses using their generators, validation and original batches. Scan adds inclusive conversion; sort interleaves original SoA input and scatters final SoA output. | Both C++ hosts compile/link. Original `2^28` workloads and conversion/reset/barrier costs remain in their operation boundaries. No native executable was launched. |
| Broader benchmark discovery | Pinned and reviewed BabelStream, HeCBench and the legacy official ParallelSort sample separately from FidelityFX SDK 1.1.4. | [Sources and semantic review](../../benchmarks/external/README.md). BabelStream memory kernels and HeCBench's reviewed CUDA/HIP kernels are not a matching native HLSL global scan/sort suite. No cross-API score or renamed synthetic workload is claimed. HeCBench kernel files with separate notices are reference-only. |
| SDK operations and selection | Application-input exclusive scan and stable full32 key/payload sort; optional GPUPrefixSums RTS/AMD backends; checked fallback; source/plan/ABI/device/driver/compiler identity and independently trusted confirmation hash. | `PrimitiveOperationTests` covers full-width/arbitrary-payload contracts, identity/capability failures, fallback and empty behavior. New `OperationDeploymentProfile` is distinct from historical paired/Unity profiles. |
| SDK execution boundary | Borrowed-buffer `D3D12OperationRecorder` records copies, transitions, dispatches and UAV barriers with caller-owned buffers, PSOs, state and completion lifetime. | Compiles without adding submission, timestamps or readback. [SDK contract](../SDK.md) specifies uploads, reuse, aliases and ownership. Caller-provided PSO/root-signature compatibility has not been exercised on a GPU. |
| Actual Unity shader consumption | Exporter copies committed shader/header/license bytes and a canonical identity manifest. SUMMIT received the actual Raw-buffer reset/scan variant and owns its explicit recorder, Structured/Raw bridge and profile-to-asset mapping. | Three committed assets verified; standalone DXC succeeds. SUMMIT reported wiring the external scan only when injected, retaining its existing fallback. This HLSL handoff does not certify Unity import/runtime, SUMMIT's whole integration, or compaction predicate equivalence. |
| Package/source completeness | Workloads NuGet includes all shared shaders, the consumer, 114 pinned upstream files and licenses, including hidden upstream files. | Zip inspection verified exactly 114 upstream files and 12 local shader assets. Git long-path and native-output path checks are provided. No old RGA/evidence bytes were changed. |
| PR/release validation and documentation | New PR/main CPU workflow builds, runs reviewed Core/Python tests, compiles shaders/native hosts, exports static contracts and checks the package. Release validation also targets the reviewed CPU test project. README retains adverse historical external comparisons with source identity. | Local checks passed. The new GitHub workflow has not been run remotely. Existing CLI/Showcase/UnifiedBench/Tuner source files match baseline exactly. |

Dependency commits were integrated sequentially:

| Contribution | Original commit | Integrated commit |
|---|---|---|
| Scan | `9019c1b47036bd2a67da03583f3e31ee6a6b87b4` | `274e5077455e6c08959a274a84379dfc4e1345dd` |
| Sort | `251392df72a5f90e0c0b773b63c7530005721492` | `61a552e2fb93c85302f0ebcf332d96a4c70499e3` |

The final validation record contains no performance result:

| Check | Observed result |
|---|---|
| Release solution build | Passed; zero managed warnings/errors |
| Reviewed Core test project | 167 passed, zero failed/skipped |
| External source/adapter Python contracts | 5 passed |
| Wave-tiled CPU model | 11 passed |
| Unified protocol Python tests | 3 passed |
| SDK and native boundary shaders | 24 entries compiled, six plan identities recorded |
| Scan compiler contracts | 20 successful builds and nine intended invalid-configuration rejections |
| Radix compiler contracts | 74 builds; maximum static shared memory 22,548 bytes |
| Native scan/sort hosts | Both compiled/linked with VS 2026 toolset v145; three C4244 warnings remain in unchanged upstream `DeviceRadixSort.cpp`, none in the local adapters |
| Native radix size contract | Exported `2^28` pairs without allocating input/GPU buffers; packed buffer is 2,147,483,648 bytes |
| Source and package checks | 114 pinned upstream files and 12 local shader assets verified; all receipt source/output hashes checked |

The shader/source locks and receipts preserve distinct identities:

- `third_party/upstream-lock.json` SHA-256:
  `8d707858e69288fa6f10c9ec208d340cce3fa73b21d0240cbd3607d9b1120334`.
- Native dependency lock SHA-256:
  `fb71bc842956ee0e048a523f00ab5d88571e254b9b4221cd2da77d93c1e875fe`.
- Pinned native DXC executable SHA-256:
  `c1547a08c4ee0cb1d8c03a65fd2a8262002e68596d152a1915b1fd8b31ed0618`.
- Verified local Workloads package SHA-256:
  `f8fdad86e36dec281c45b942c8d9d8bf4428f45798d7b0362ea04f04d6f9a7dc`.

The canonical SUMMIT export remains valid at source commit
`274e5077455e6c08959a274a84379dfc4e1345dd`; its three asset bytes are unchanged
at the final code commit. Asset SHA-256 is
`117f226d4f83c9a19155d03f2d6179e55db676e19fc58c76ccb1aaef836b6f16`,
variant `hlslperf.scan-wave-tiled.u32.wave32.g256.b4096.v1`,
ABI `hlslperf.raw-buffer.v1`. The manifest limits the consumer to 16,776,960
elements, requires D3D12/SM6.6/wave32, and specifies preallocated scratch of
`8 + 12*ceil(capacity/4096)` bytes. Empty calls skip both dispatches and preserve
sentinels. SUMMIT's Structured/Raw bridge has explicit copy/reset/scan/copy cost;
that cost has not been measured.

Reproduce the build and CPU checks using the explicit steps in
[cpu-validation.yml](../../.github/workflows/cpu-validation.yml). Native
preparation uses `tools/prepare_external_benchmarks.py --output <new-short-dir>
--build`, adding `--toolset v145` for the tested VS 2026 installation. Its only
execution mode is preparation/build. It verifies pinned NuGet bytes and does not
run package build hooks or generated executables. The consumer exporter similarly
requires a new output directory and does not import Unity.

Future native GPU/performance commands are `scan.exe run-upstream`,
`scan.exe run-candidate`, `sort.exe run-upstream` and `sort.exe run-candidate`,
run from the corresponding executable directory. No arguments or `--help`
only prints usage. These explicit commands retain upstream tests and batches;
they were not executed in this repair. A future result must be labeled an
adapted upstream benchmark and include its actual source/runtime identity.
The native validators' restricted scan domain and payload checks do not replace
full-width overflow and duplicate-stability validation on the GPU.

Historical performance material remains tied to its old source, including the
prior [unified comparison](UNIFIED_BENCHMARK_RESULTS.md) at
`824cfafc9a07ade0a3cf440c1f5b3a19af2e8bf0`. Compilation and CPU models do not
transfer those measurements to the new kernels or establish speedup.
