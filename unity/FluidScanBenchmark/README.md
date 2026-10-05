# Fluid application scan benchmark (code prepared; performance unmeasured)

This integration uses Sebastian Lague's **Fluid ScreenSpace 2** scene: two colliding
volumes of water with screen-space water rendering and foam/spray. It substitutes
only the exclusive scan inside spatial count sorting. No Unity session, GPU
correctness test, performance run, or visual capture is invoked by preparation.

| Explicit arm | Implementation |
| --- | --- |
| `original` | The author's recursive Blelloch exclusive scan, recorded into a CommandBuffer |
| `hlsl-wave-tiled` | This repository's actual wave32/4096-element exclusive kernel |
| `gpuprefixsums-rts` | Unmodified pinned GPUPrefixSums Unity Reduce-Then-Scan exclusive kernel and host |

These are three scans in the same count sorter, rather than three different sorting
algorithms. The project adaptation derives its underlying algorithm from
GPUPrefixSums; the external arm measures the unchanged upstream RTS implementation.
The earlier `2^28` inclusive result is not a prediction for this application.

## Prepare source only

From the HLSL repository root, either use a local source repository containing the
pinned commit, or explicitly download it:

```powershell
python tools/prepare_fluid_benchmark.py --fluid-source C:/path/to/Fluid-Sim --output .scratch/FluidScanApp
# Alternatively: downloads source only, never launches Unity.
python tools/prepare_fluid_benchmark.py --download --output .scratch/FluidScanApp
```

The destination must be new. Preparation extracts committed upstream bytes,
verifies all five replaced input files and all five vendored RTS files against
`dependencies.json`, applies the overlay, and emits a payload/source identity
manifest. Dirty source repositories are not modified; their working-tree files
are not used as the upstream snapshot. The local HLSL shader bytes are hashed and
the manifest explicitly records whether those integration sources are modified.

Pins:

- Fluid-Sim: `4717b7259718d349b0001c82836f24ce5fec81d7`, MIT.
- GPUPrefixSums: `98d93a4e9ed2f3c8353119515bf9be90a2e137ad`, MIT.
- The wave-tiled header comes from this checkout, with its upstream attribution intact.

The original project targets Unity 2022.3.46f1. C# API compilation is checked
against installed Unity 2022.3 references without invoking the editor. The HLSL
arm additionally requires a Unity DXC importer capable of SM6.6/fixed wave32.
Offline DXC compilation does **not** establish Unity import or device support.
All arms must use the same editor/player version and D3D12 backend when eventually
built. Unsupported kernels fail explicitly; no alternate backend is relabeled.

## Deferred build and execution

The following commands are instructions for a later authorized hardware session.
They are never executed by the installer or the static checker.

Build a Windows Development Player with only the author-provided scene:

```powershell
& 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe' `
  -batchmode -quit -projectPath "$PWD/.scratch/FluidScanApp" `
  -executeMethod HlslPerf.FluidBenchmark.Editor.FluidBenchmarkBuild.Build `
  --fluid-build-output "$PWD/.scratch/FluidScanPlayer/FluidScan.exe" `
  -logFile "$PWD/.scratch/fluid-build.log"
```

No benchmark flags means the ordinary original simulation path. Every benchmark
or GPU correctness invocation requires explicit mode, arm and a fresh output path:

```powershell
# GPU validation only: no simulation Start/Update and no measured frames.
& .scratch/FluidScanPlayer/FluidScan.exe -force-d3d12 `
  --fluid-validate-only --fluid-arm hlsl-wave-tiled --fluid-output .scratch/fluid-validation

# A future single-arm raw measurement, after hardware execution is authorized.
& .scratch/FluidScanPlayer/FluidScan.exe -force-d3d12 `
  --fluid-benchmark --fluid-arm original --fluid-output .scratch/fluid-original-01 `
  --fluid-seed 42 --fluid-dt 0.016666667 --fluid-warmup 120 --fluid-frames 600 --fluid-spawn-density 600
```

Run each arm in a fresh process; balance/repeat the order and retain failures.
Initial spawn jitter uses a fixed seed. Camera input, simulation input and VSync
are disabled in benchmark mode; the simulation uses fixed dt and the scene's
three substeps. Resolution is requested at 1920x1080 and actual dimensions are
exported. Foam and rendering settings remain the pinned scene settings. The
scene's saved debug count is 410,758 water particles and its foam capacity is
1,024,000; `run.json` records actual water count and capacity, not active foam count.

## Complete-operation boundaries

One graphics-queue CommandBuffer records each complete simulation frame. Every
recursive scan and sorting parameter/buffer binding is recorded in that command
stream. Unity owns resource dependencies and UAV barriers between commands;
real inter-dispatch visibility remains a hardware correctness gate.

| GPU sample | Included work, accumulated per simulation frame |
| --- | --- |
| `FluidBenchmark.Scan.Complete` | Complete scan; packing/copies, padded RTS work, reset and unpacking where required |
| `FluidBenchmark.CountSort.Complete` | Clear counts/initialize IDs, histogram, scan, atomic scatter, copyback |
| `FluidBenchmark.SpatialHash.Complete` | Count sort plus offset initialization/construction; excludes the preceding key-generation kernel |
| `FluidBenchmark.Simulation.Complete` | All solver substeps, key generation, reordering, density/pressure/optional viscosity, position update, foam update/copyback |

Original scan is in-place structured uint32. Wave-tiled input/output/scratch are
distinct raw allocations. RTS input/output are uint4 buffers with zero-filled
padding and a spare capacity word-group for its strict host bound; output is copied
back into the caller's original count buffer. No adapter upload/readback occurs
inside the measured simulation. Allocation and full-output readback belong to
startup validation/warmup, outside GPU sample ranges.

All arms preserve the original count-sort atomic scatter. Equal-key particle order
can vary, so fixed-seed scenes need not remain pixel-identical after many steps.
Correctness checks sorted keys, source key/index association and a full permutation;
it does not invent a stable ordering guarantee.

## Correctness gate and raw evidence

Before any simulation starts, the selected arm checks 80 complete scan cases:
empty/singleton, odd lengths, 512/3072/4096 boundaries, tails, full uint32 wraparound,
zeros, repeated growth/shrink and guard-word preservation. Eighteen complete sorter
cases check every key/index, the permutation and every spatial offset, including all-equal/max keys.
Failure writes `correctness.json`/`failure.txt`, stops scene scripts and exits 2.
This gate is implemented but **not executed** in the code-preparation task.

Successful future measurement exports:

- `correctness.json`: selected-arm full-output GPU checks, outside timing.
- `observations.csv`: wall frame intervals and delayed GPU sample observations.
- `frame-timing-diagnostics.csv`: deduplicated FrameTimingManager diagnostics.
- `run.json`: device/API/driver, scene settings, measurement interval and source identity.

The GPU Recorder API documents a three-frame delay. Rows retain both observed and
assumed source Unity frame; block counts must equal the expected substep count.
After the measured simulation stops, the collector drains eight frames. Missing,
zero or mismatched GPU fields are blank/`unavailable`, never reported as zero time.
Hardware review must confirm delayed frame association before treating these rows
as an accepted GPU distribution. FrameTimingManager has a separate delayed stream;
its records are diagnostic only and are never paired by row index or summed with
CPU timing. Wall timing spans successive LateUpdates and includes collector,
rendering and presentation overhead. This is a comparison harness, not a claim
that development-player overhead equals a release application.

```powershell
python tools/analyze_fluid_benchmark.py .scratch/fluid-original-01 --output .scratch/fluid-original-summary.json
```

The analyzer reports p50/p95 for complete wall intervals, counts unavailable
samples, and labels GPU distributions provisional until the runtime association
has been verified. It makes no inter-process speedup or 60 FPS capacity claim.
Those require a balanced cohort and consistent actual resolution/quality. Record
arms separately and compose the split screen afterward, so two simulations do
not compete during timing.

## Static checks (no Unity/GPU)

```powershell
python tools/test_fluid_benchmark.py
python tests/test_wave_tiled_scan.py
python tools/check_fluid_benchmark.py --project .scratch/FluidScanApp `
  --unity-editor 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor' `
  --mathematics-source C:/path/to/com.unity.mathematics-1.2.6/package/Unity.Mathematics `
  --csc 'C:/Program Files/dotnet/sdk/10.0.201/Roslyn/bincore/csc.dll' `
  --dxc 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/dxc.exe' `
  --output .scratch/fluid-static-check
```

The static checker compiles the real mathematics source, every generated runtime
and editor C# file against real Unity reference assemblies, and all changed/reused
scan/sort compute entries with DXC. It never instantiates Unity classes, opens a
GPU device, imports a Unity project, builds a Player or dispatches a kernel.

The [frozen October 5 static receipt](../../docs/evidence/fluid-scan-static-20261005.json)
records 30 C# sources, 29 compute entries, 16 pure settings checks, seven harness
tests and 12 existing deterministic wave-model tests. It retains the author's
unused-field warning and the unmodified RTS compiler warnings. The bridge/wave
entries compile with warnings-as-errors. Every GPU/Unity execution field remains false.
