# Bounded RTX 4090 ultra-scale scan experiment

This is a separate extension of the frozen native inclusive experiment. It does
not modify the historical 2^28 protocol, vendored sources, scan kernels, tuning
parameters, shader profiles or upstream `TimeScan` implementation.

Four fixed uint32 element counts: 67,108,864; 134,217,728; 201,326,592;
268,435,456. Measure inclusive and exclusive separately, comparing native
`tile-fused` (wave32/group256/items16/partition4096/polls4/persistent256) with
the pinned upstream RTS defaults. The exclusive branch uses the same local
exclusive kernel as the earlier direct-buffer fluid path. This is a native
D3D12 experiment, not another Unity/application-frame experiment.

The input is the unchanged upstream GPU `InitOne`. Every warmup and measured
operation regenerates the entire input outside its GPU timestamps. Eight
warmups and 100 measured operations per fresh process; each operation executes
and waits before the next submission. The timestamp interval includes the
complete scan hook and dependencies, including local lookback state reset.
Compilation, allocation, input generation, validation, query readback and CPU
submission/fence time are excluded. Both paths consume and produce native
compatible buffers; there is no pack/unpack bridge.

The gate runs existing full32 CPU-oracle cases at smaller boundary lengths and
both semantics plus GPU validation of every ultra-scale output element. Pilot
processes run every size/semantic/arm; pilots are retained and excluded from
confirmation. Confirmation uses six fresh processes per arm/cell (96 total).
Sizes ascend to check resource pressure before escalation; pair orders alternate
so each arm occurs first three times. Inclusive/exclusive order also alternates.
No parameter search, timing exclusions, best-run selection or retries for wins.

Statistics use paired process means: six within-round RTS/local ratios per cell,
nominal pointwise 95% Student-t df5 intervals in log space. There is no
multiplicity adjustment; intervals do not establish an entire continuous range.
Report variability and every negative result. Correlated inner iterations are
not independent experimental replicates. User applications and GPU clocks are
uncontrolled. The current driver differs from the historical experiment, so old
and new observations must not be pooled.

Safety policy:

- Hard element cap 2^28; no larger allocation even if GPU VRAM is available.
- Forecast 12 bytes/element +64 MiB before each child/allocation, retaining at
  least 4 GiB host available physical and commit memory and 8 GiB GPU free.
- Total NVIDIA-reported GPU use is capped at 12 GiB; stop at 80 C.
- Shared `Local\\CodexR9700VNextUnityGpu` mutex, one own child at a time, hidden
  console, five seconds of rest between processes, no simultaneous GPU jobs
  launched by this experiment. The rest was added after the first campaign
  stopped on a host commit preflight; that interrupted campaign is retained and
  is not pooled with the resource-recovery campaign.
- 60-second own-child deadline and 100 ms per-operation GPU-time stop threshold.
  Resource telemetry is checked during execution; any correctness, device,
  source-identity, telemetry or resource failure stops the entire stage.
- Keep every failed attempt. Terminate only the child started by this runner.
  No TDR, power, clock, page-file or user-application changes. These guards reduce
  risk; they cannot guarantee recovery from a GPU/driver hang.
- Large correctness checks run on the GPU and read back only the error count,
  avoiding multi-GiB host arrays. The postcheck validates the actual output left
  by the final timed operation without rerunning the scan.

The local wrapper initializes the inherited host size field before its first
allocation; the vendored class has no initializer for that field. This does not
change dispatches, input generation, timing or validation kernels.

Reproduction (all GPU work and build acquire the shared mutex automatically):

```powershell
python tools/test_ultra_scan.py
python tools/test_inclusive_scan_analysis.py
python tools/run_ultra_scan.py build --output .scratch/ultra-native --msbuild 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe'
python tools/run_ultra_scan.py probe --native .scratch/ultra-native --output .scratch/ultra-probe
python tools/run_ultra_scan.py gate --native .scratch/ultra-native --device .scratch/ultra-probe/device.json --output .scratch/ultra-gate
python tools/run_ultra_scan.py pilot --native .scratch/ultra-native --device .scratch/ultra-probe/device.json --output .scratch/ultra-pilot
python tools/run_ultra_scan.py confirm --native .scratch/ultra-native --device .scratch/ultra-probe/device.json --gate .scratch/ultra-gate --pilot .scratch/ultra-pilot --output .scratch/ultra-confirmation
python tools/run_ultra_scan.py analyze --evidence .scratch/ultra-confirmation --output .scratch/ultra-analysis.json
```

Use fresh output paths on reruns; historical evidence is never overwritten.
To complete only the two previously unfinished scales, pass
`--counts 201326592,268435456 --rest-seconds 10` consistently to new gate,
pilot and confirmation stages. This declares 48 fresh confirmation processes;
it is a separate cohort and is not pooled with the older interrupted stage.
Rest can be increased within 5..30 seconds, but resource/timing guards cannot be
relaxed. The analyzer verifies the declared interval against process timestamps.
If a resource check rejects a child before launch and the stage stops, an explicit
CPU-only `analyze --partial` audit can retain cells with all six pairs. It verifies
the rejection against recorded telemetry, marks the campaign incomplete, rejects
any process after the stop, and does not compute intervals for unfinished cells.
This never converts an interrupted stage into a successful full confirmation.
