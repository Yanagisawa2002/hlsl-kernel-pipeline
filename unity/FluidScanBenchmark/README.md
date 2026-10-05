# Fluid application scan benchmark

The [scale and integration diagnosis](../../docs/results/fluid-scan-scaling-rtx4090-20261005.md)
separates core scan, complete adaptation and application wall time. It adds a raw
count-buffer path consumed directly by the sorter, an isolated 32,768–16,000,000
element sweep and seven application sizes. The earlier cohort below remains frozen.

[RTX 4090 hardware results, October 5](../../docs/results/fluid-scan-rtx4090-20261005.md):
410,758 particles, 1080p, three independent runs per arm. Complete-scan p50/frame
is 0.0512 ms original, 0.058368 ms local wave-tiled, 0.037888 ms external RTS.
The local adaptation is **14% slower** at this size. Whole-frame p50 is 6.9119,
6.9579 and 6.9893 ms respectively; no clear whole-frame gain is established.
This scene currently fails the gate for a local-scan acceleration showcase.

This integration uses Sebastian Lague's **Fluid ScreenSpace 2** scene: two colliding
volumes of water with screen-space water rendering and foam/spray. It substitutes
only the exclusive scan inside spatial count sorting. No Unity session, GPU
correctness test, performance run, or visual capture is invoked by preparation.

| Explicit arm | Implementation |
| --- | --- |
| `original` | The author's recursive Blelloch exclusive scan, recorded into a CommandBuffer |
| `hlsl-wave-tiled` | This repository's actual wave32/4096-element exclusive kernel |
| `gpuprefixsums-rts` | Unmodified pinned GPUPrefixSums Unity Reduce-Then-Scan exclusive kernel and host |
| `hlsl-wave-tiled-direct` | The same local scan, with raw histogram input and direct raw-prefix consumption; no scan pack/unpack |

All paths retain the author's counting-sort algorithm. The direct path changes
only the count/prefix buffer layout and producer/consumer bindings. The project adaptation derives its underlying algorithm from
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

The original project targets Unity 2022.3.46f1. Hardware runs use Unity
6000.3.13f1 and D3D12, common to all arms. Unity's DXC importer targets SM6.0:
the generated wrapper omits the SM6.6 WaveSize attribute, probes all 256 lanes
for native width 32, and guards the actual shared scan entry against other widths.
It retains the same wave32 algorithm and provides no alternate scan fallback.
The ordinary repository SM6.6 consumer remains unchanged. The vendored RTS files
remain pinned; its generated root declares the VULKAN keyword because Unity
ignores its pragma in an ordinary include. Upstream kernel and host operations
are retained. Offline DXC compilation alone does **not** establish runtime support.
All arms must use the same editor/player version and D3D12 backend when eventually
built. Unsupported kernels fail explicitly; no alternate backend is relabeled.

## Build and execution

The following commands are instructions for an explicit hardware session.
They are never executed by the installer or the static checker.

Build a Windows Development Player with only the author-provided scene:

```powershell
& 'C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Unity.exe' `
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

# A single-arm raw measurement with the optional Unity Recorder path.
& .scratch/FluidScanPlayer/FluidScan.exe -force-d3d12 `
  --fluid-benchmark --fluid-arm original --fluid-output .scratch/fluid-original-01 `
  --fluid-seed 42 --fluid-dt 0.016666667 --fluid-warmup 120 --fluid-frames 600 --fluid-spawn-density 600
```

The Player must have a visible, unobscured backbuffer for complete rendering.
Hidden-window runs are pilot records and are excluded from the reported cohort.
Graphics Jobs are disabled for all arms. The build retains the three hidden blur
shaders used by the author's Shader.Find helpers. All Unity error/assert/exception
logs fail the correctness or measurement gate.

### Explicit native GPU timestamps and offline visual export

Unity Recorder returned no GPU samples on the tested built-in pipeline. To use
the measured D3D12 query path, build the small native plugin explicitly first
(Windows x64, Visual Studio C++ Build Tools and installed Unity PluginAPI headers):

```powershell
python tools/build_fluid_timing.py --unity-editor 'C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor' --output .scratch/fluid-timing
python tools/prepare_fluid_benchmark.py --fluid-source C:/path/to/Fluid-Sim --timing-plugin .scratch/fluid-timing/FluidGpuTiming.dll --output .scratch/FluidScanNative
# Build the above project, then run the Player normally with:
# --fluid-benchmark --fluid-arm original --fluid-output NEW_DIRECTORY --fluid-gpu-timing d3d12-query
# The other arms use exactly the same Player and timing method.
```

The plugin records 26 D3D12 timestamps per simulation frame on Unity's active
graphics command list. Five nested operation boundaries accumulate three core-scan,
complete-scan, sort and spatial calls and one simulation call. The isolated sweep
configures 32 core/complete calls and one batch range per frame. Only initialized
query ranges are resolved. It resolves to a 128-frame ring,
reads only after the corresponding Unity frame fence completes, and stores the
source frame explicitly. It does not submit a separate queue, flush GPU work or
block for timing readback. Frequency comes from the same graphics queue. In this
mode `gpuDelayValidated` is the legacy field for completed source-frame association;
`gpuTimingMethod` identifies the query path and `appliedGpuRecorderDelayFrames=-1`.
Native errors abort the run; unavailable samples never become zero milliseconds.

For separate visual exports after measurements, use `--fluid-capture` instead of
`--fluid-benchmark`, with the same arm/output/seed/density/timestep flags.
`--fluid-frames 600` exports 600 PNGs plus `capture.json` from the initial state,
without performance samples. These are fixed-step offline frames; playback at
60 fps is not a measured frame-rate claim. Encode each arm separately and compose
the split screen afterward. Recorders and native timing are disabled in capture mode.

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
| `FluidBenchmark.Scan.Core` | Required reset plus the entire exclusive-scan algorithm, including recursive or multi-dispatch work; excludes scan pack/unpack |
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

For `hlsl-wave-tiled-direct`, the histogram writes a raw uint32 count buffer and
`IExclusiveScan.Record` returns a distinct raw prefix buffer. The sorter binds that
returned buffer directly for atomic scatter. Input remains unchanged by the scan;
input/output aliasing is never used. The existing sorted-key/item copyback stays
inside complete sort timing. RTS still uses its structured/uint4 bridges, so its
complete timing and the direct local timing have different integration costs;
compare the core column as well. RTS allocates reusable capacity but dispatches
only the current padded logical length after growth or shrink.

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
The scale session additionally validates all four arms. After application timing
stops and samples drain, every final particle position is checked for finite
coordinates; this readback is outside the measured window. This verifies finite
execution, without asserting trajectory or image equivalence across unstable sorts.

Successful measurement exports:

- `correctness.json`: selected-arm full-output GPU checks, outside timing.
- `observations.csv`: wall frame intervals and delayed GPU sample observations.
- `frame-timing-diagnostics.csv`: deduplicated FrameTimingManager diagnostics.
- `run.json`: device/API/driver, scene settings, measurement interval and source identity.

For the optional Unity Recorder path, its API documents a three-frame delay. Rows retain both observed and
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

`tools/compare_fluid_benchmark.py RUN_DIRS... --output NEW_JSON` checks identical
settings/source/timing identities, complete accepted samples, independent records
and at least three equal repeats per arm. It reports median per-run p50, repeat
ranges and median per-run p95, without inferring significance or particle capacity.

## Scale and compatible-buffer experiment

These are explicit GPU launches; preparation and static checks never launch them.
Use a newly built Player containing the native timing plugin and fresh output roots:

```powershell
python tools/run_fluid_scaling.py gate --player C:/path/to/FluidScan.exe --output .scratch/scale-gates
python tools/run_fluid_scaling.py sweep --player C:/path/to/FluidScan.exe --output .scratch/scale-sweep
$runs = Get-ChildItem .scratch/scale-sweep -Directory | ForEach-Object { $_.FullName }
python tools/analyze_fluid_sweep.py $runs --output .scratch/scale-sweep.json
python tools/run_fluid_scaling.py application --player C:/path/to/FluidScan.exe `
  --output .scratch/scale-application --densities 60,200,600,1500,6000,12000,23000 --app-frames 120
python tools/analyze_fluid_application_scaling.py .scratch/scale-application --output .scratch/scale-application.json
```

The launcher saves the complete process plan and Player file hashes before execution,
uses four cyclic arm orders in fresh serial processes, retains every attempt and
holds the cooperative GPU experiment mutex. Normal visible Player windows preserve
rendering; no video capture is active. The 120-frame application measurement follows
120 warmup steps, covering the same simulated 2–4 seconds for every arm/size. The
default application window is 600 measured frames; pass 120 explicitly to reproduce
the scale cohort. Densities map to actual counts recorded in `run.json`; increasing
density with fixed scene physics changes neighbor load and does not establish a
constant-physical-quality or 60 FPS capacity comparison.

An individual sweep is also available as `--fluid-scan-sweep --fluid-arm ARM
--fluid-output NEW_DIRECTORY --fluid-gpu-timing d3d12-query --fluid-lengths
32768,410758,16000000 --fluid-scan-batch 32 --fluid-warmup 60 --fluid-frames 120`.
Length is limited by the shared clear/fill dispatch to 16,776,960. Each operation
regenerates `uint32(index * 747796405 + 2891336453)` outside both scan ranges.
32 operations are timed per frame, with full-size output/guard checks before and
after timing at each length. Core includes reset plus the full algorithm, rather
than one selected dispatch. Sweep timings are milliseconds per operation; fluid
scan timings accumulate three calls per simulation frame. The sweep runs no solver
or rendering and makes no application-frame claim. Inputs are regenerated in a
sequential hot workload; this is not a cold-cache bandwidth experiment.

Source-frame timestamps include common harness/command-stream effects and required
dependencies. They are not overhead-free kernel busy time. Analysis reports p50/p95
descriptively, equally weights independent process means, and uses four paired
process-mean log ratios for nominal pointwise Student-t intervals (df=3). These
intervals are not corrected across lengths/metrics. Adaptation attribution uses
paired per-frame `complete - core` values; subtracting independent medians can give
a misleading decomposition. An `invalid-attempt.json` excludes an entire marked
execution root from analysis, preserving its negative evidence.

```powershell
python tools/test_fluid_sweep.py
python tools/test_fluid_application_scaling.py
```

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
is the earlier preparation snapshot: 30 C# sources, 29 compute entries, 16 pure settings checks, seven harness
tests and 12 existing deterministic wave-model tests. It retains the author's
unused-field warning and the unmodified RTS compiler warnings. The bridge/wave
entries compile with warnings-as-errors. Every GPU/Unity execution field remains false.
