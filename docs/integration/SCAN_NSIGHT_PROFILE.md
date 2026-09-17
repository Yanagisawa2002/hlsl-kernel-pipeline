# Marker-isolated RTX 4090 scan profiling

This profiling path exists only to explain the already-frozen inclusive-scan timing result. It does not replace the September 15 paired benchmark and it does not change any scan shader or algorithm.

## What `profile-once` does

For `rts`, `tile`, or `tile-fused`, the native host now:

1. creates the same pinned `2^28` uint32 workload;
2. executes one unmarked inclusive-scan warmup;
3. reinitializes the input to all ones;
4. records one complete inclusive scan inside the GPU marker `HlslPerf.ScanInclusive.ProfileRange`;
5. submits and waits for that scan;
6. validates the full-size result after the marker.

The marker wraps the existing `PrepareScanCmdListInclusive()` hook. Therefore it includes all scan-operation dispatches/barriers already charged by the frozen timestamp benchmark: RTS for `rts`; reset + wave-tiled scan + AddInput for `tile`; reset + native-inclusive wave-tiled scan for `tile-fused`. Input generation, CPU recording/submission, fence wait and validation are outside the marker.

The marker uses the stable `WinPixEventRuntime.dll` command-list ABI and is loaded dynamically only for `profile-once`. Normal validation and timing modes have no PIX runtime dependency.

## Build a fresh native host

Use a new output directory so the preparation receipt remains auditable:

```powershell
python tools/prepare_external_benchmarks.py `
  --output .scratch/native-profile `
  --programs scan `
  --build `
  --msbuild 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe'
```

If your MSBuild installation is elsewhere, substitute its real path.

## Install the PIX event runtime

Microsoft distributes `WinPixEventRuntime` as a NuGet package. The current NuGet Gallery version at the time this profiling path was added is `1.0.240308001`. The package contains `WinPixEventRuntime.dll`; place the x64 desktop DLL next to:

```text
.scratch/native-profile/scan/scan.exe
```

Alternatively, put the DLL on `PATH`. The host intentionally fails instead of silently running without a marker if the DLL or stable marker exports cannot be loaded.

Official runtime documentation: <https://devblogs.microsoft.com/pix/winpixeventruntime/>
NuGet package: <https://www.nuget.org/packages/WinPixEventRuntime/>

## Get the exact RTX 4090 identity

If you already have the device file from the September 17 run, reuse it. Otherwise probe the freshly built host:

```powershell
python tools/run_inclusive_scan.py probe `
  --native .scratch/native-profile `
  --adapter 'NVIDIA GeForce RTX 4090' `
  --output .scratch/profile-probe
```

Then:

```powershell
$device = Get-Content .scratch/profile-probe/device.json | ConvertFrom-Json
$exe = (Resolve-Path .scratch/native-profile/scan/scan.exe).Path
$device.adapter
$device.luid
```

## Preflight each arm outside Nsight

Run these once from PowerShell before profiling. Each successful process must print an `HPJSON` `profileRange` record with `validated:true`.

```powershell
& $exe profile-once tile       268435456 $device.luid $device.adapter
& $exe profile-once tile-fused 268435456 $device.luid $device.adapter
& $exe profile-once rts        268435456 $device.luid $device.adapter
```

Do not continue if any arm reports `RUNTIME_FAILED`, validation failure, a different adapter, or a missing PIX runtime.

## Nsight Graphics capture

Create three separate GPU Trace projects/captures, one per arm. Launch `scan.exe` directly, not the Python wrapper, so the profiled process is the D3D12 workload itself.

Executable:

```text
<repo>/.scratch/native-profile/scan/scan.exe
```

Working directory:

```text
<repo>/.scratch/native-profile/scan
```

Arguments, one capture at a time:

```text
profile-once tile 268435456 <LUID> "NVIDIA GeForce RTX 4090"
profile-once tile-fused 268435456 <LUID> "NVIDIA GeForce RTX 4090"
profile-once rts 268435456 <LUID> "NVIDIA GeForce RTX 4090"
```

For the first pass, use Throughput Metrics and Real-Time Shader Profiler. Keep other GPU-active applications closed and use the same clock-lock policy for all three arms.

In the resulting trace, verify that the compute queue contains exactly one marker named:

```text
HlslPerf.ScanInclusive.ProfileRange
```

Select/zoom that marker and confirm its child dispatch structure:

- `tile`: reset + wave-tiled scan + `AddInput`;
- `tile-fused`: reset + wave-tiled inclusive scan, no `AddInput`;
- `rts`: the pinned ReduceThenScan dispatch sequence.

Nsight Graphics documents that GPU Trace displays API-specific user markers on the queue timeline and that Multi-Pass Metrics relies on consistent user markers across frames/passes. Use the marker as the analysis boundary; do not interpret whole-capture averages as scan-only values.

## Multi-pass follow-up

After the marker appears correctly in all three ordinary traces, try Multi-Pass Metrics. The previous submit-limited attempt was invalid because that collection mode could not form a stable repeated frame/range. The new profile command provides a deterministic marker hierarchy, but whether the installed Nsight build accepts the frameless process for multi-pass collection must still be verified locally.

If Multi-Pass Metrics succeeds, export metrics for the marker range and preserve the exact exported counter names. If it fails, keep the ordinary marker-isolated throughput/shader traces; do not manufacture missing counters.

## Evidence to keep

For each arm preserve:

- `.ngfx-gputrace` report;
- exported metrics/CSV if available;
- screenshot showing the marker and dispatch children;
- stdout log with the `profileRange` validation record;
- source commit, `preparation.json`, GPU/driver identity and Nsight version;
- whether clocks were locked and whether unrelated GPU work was visible.

The frozen September 15 timestamp benchmark remains the performance result. These captures exist to attribute the mechanism more precisely.
