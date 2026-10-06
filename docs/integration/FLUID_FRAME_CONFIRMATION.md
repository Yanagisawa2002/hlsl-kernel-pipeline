# Independent fluid whole-frame confirmation

This cohort tests the application result independently of the native ultra-scale
scan timings. It reuses the frozen successful Unity 6000.3.13f1 Development Player
from the fluid scaling experiment, with all Player files verified against that
experiment's plan. Neither scan kernels nor the Unity scene are changed.

Two fixed, previously validated loads: spawn densities 1500 and 23000, producing
1,024,000 and 15,761,198 water particles. Original author's scan versus local
`hlsl-wave-tiled-direct`, with compatible raw producer/consumer buffers and no
scan pack/unpack. This application requires exclusive scan; three calls accumulate
per simulation frame. The isolated inclusive benchmark is not its caller.

1920x1080, the same water/foam scene, camera, shader quality, physics, seed 42,
dt 1/60 and three substeps. 120 warmup and 120 measured fixed steps, corresponding
to the existing simulated 2..4-second window. No recording, headless run,
minimized-backbuffer timing or changed settings. Rendered/presented wall frame is
the primary endpoint; GPU scan, sorting, spatial update and complete simulation
are secondary diagnostics. GPU simulation does not include water/foam rendering.

The new pilot uses one process per size/arm (four total), excluded from inference.
Confirmation uses six independent processes per arm/size (24 total). Each pair's
arm order alternates; each arm runs first three times. Sizes ascend. Each process
passes existing full-output scan/sort/spatial-offset checks and full final-position
finite validation. The analyzer verifies every source-frame observation, all six
nested metrics, raw hashes, process receipts and nonoverlapping timestamps.

The candidate and counts are fixed before confirmation. No retries for wins,
outlier filtering, selected windows or fastest-run selection. Report every result.
Six paired process means produce nominal pointwise 95% Student-t df5 log-ratio
intervals, without multiplicity correction. Correlated frames are not independent
replicates. A practical whole-frame gain is defined in advance as a time-reduction
interval lower bound of at least 1%; smaller significant changes are reported
without meeting that practical criterion.

Resource policy:

- One own child and the shared `Local\\CodexR9700VNextUnityGpu` mutex; ten seconds
  between processes. An abandoned lock fails rather than starting more GPU work.
- Hard cap 16,000,000 particles, restricted to the two declared loads. No attempt
  to simulate the native benchmark's 201M/268M particles on this device.
- Conservative fluid allocation forecast 256 bytes/particle +256 MiB. Retain
  4 GiB available host physical/commit and 8 GiB GPU free. Total GPU use at most
  12 GiB; stop at 80 C. Host checks every 0.5 s, NVIDIA telemetry about every 2 s.
- Own-child deadline 180 seconds. Reject a completed run if any measured simulation
  GPU interval exceeds 1000 ms or wall frame exceeds 2000 ms. Timing limits are
  post-run checks; this is not a per-dispatch watchdog or GPU-hang recovery guarantee.
- Any resource, telemetry, correctness, runtime identity or device failure stops
  the stage. Preserve attempts; terminate only this runner's child. No changes to
  TDR, power, GPU clocks, page file or other applications.

Background desktop work, GPU clocks and atomic equal-key sorting are uncontrolled.
Equal configurations and finite positions do not establish equal trajectories or
images. Frame differences cannot automatically be attributed to scan alone. Native
all-one scan results and previous application cohorts are not pooled into this
confirmation. The tested fluid scan lengths remain below the native ultra-scale
points, so this cohort cannot establish application transfer at 201M/268M.

The retained October 6 confirmation stopped on a reported 80 C thermal trip after
20 completed processes: the 1,024,000-particle cell has six pairs; the larger cell
has four, with no confirmation interval. The explicit CPU-only `--partial` audit
verifies the stopped prefix and absent tail and preserves `complete=false`.
The measured legacy launcher checked resource limits before appending telemetry,
so its rejected temperature point is missing (last retained point 78 C). Keep
that limitation and its measured source snapshot. The current launcher appends
the point before checking; this post-stop logging fix does not rewrite old data.
No GPU retry was made after the stop.

Reproduce with a frozen Player and its complete reference manifest:

```powershell
python tools/test_fluid_frame_confirmation.py
python tools/test_fluid_application_scaling.py
python tools/test_fluid_benchmark.py
python tools/run_fluid_frame_confirmation.py pilot --player PATH/FluidScan.exe --reference-plan PATH/application-02/plan.json --output .scratch/fluid-frame-pilot
python tools/run_fluid_frame_confirmation.py confirm --player PATH/FluidScan.exe --reference-plan PATH/application-02/plan.json --pilot .scratch/fluid-frame-pilot --output .scratch/fluid-frame-confirm
python tools/analyze_fluid_frame_confirmation.py --evidence .scratch/fluid-frame-confirm --pilot .scratch/fluid-frame-pilot --output .scratch/fluid-frame-analysis.json
```

Always use fresh output paths. The Player can be rebuilt using the existing fluid
preparation/build workflow, but a rebuilt Player has a separate identity and needs
its own complete reference manifest and pilot; do not reuse this cohort's identity.
