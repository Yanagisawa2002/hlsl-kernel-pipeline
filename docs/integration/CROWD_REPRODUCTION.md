# Portable Crowd/VFX validation and measurement

This runs the existing controlled renderer with four exclusive-compaction paths.
It does not require Unity, a native C++ benchmark host, or an MSVC build. Do not
run the historical 18-process inclusive primitive experiment to reproduce this
application task.

## Dependencies and host requirements

| Component | Validated local version / requirement |
|---|---|
| OS | Windows 11, build 26200; a hardware D3D12 adapter exposed through DXGI |
| CPU/RAM | Core Ultra 7 265K, 20 cores, 33,682,857,984 physical bytes; other CPUs require fresh measurements |
| .NET | SDK **10.0.302**, runtime **10.0.10**, x64; see `global.json` |
| Managed bindings | Vortice.Direct3D12 / DXGI / Dxc **3.8.3**; DirectX 3.8.3, Mathematics 2.1.0 |
| Shader compiler | Vortice.Dxc.Native **1.0.5**, Windows x64 DXC DLL **1.9.2602.17**; the runner records the actual loaded DLL hash |
| Other managed dependencies | SharpGen.Runtime and SharpGen.Runtime.COM **2.4.2-beta** |
| Python | Python 3, standard library only for orchestration/statistics |
| GPU tools | `nvidia-smi` for this NVIDIA campaign's load/VRAM gate |
| GPU functionality | SM **6.7** for unchanged RTS; SM6.6 fixed **wave32** for local/fused code; raw UAV buffers and D3D12 timestamp queries |
| Device selection | Set `HLSLPERF_CROWD_ADAPTER` to the exact DXGI name; default is NVIDIA GeForce RTX 4090. No automatic WARP or alternate-adapter selection |
| Capacity | At largest tested N, about 175 MB of committed default buffers plus about 6.3 MB reusable readback; declared arm cap 512 MiB |
| Isolation budget | At least 8 GiB free VRAM, 4 GiB free host RAM, and 30 GiB free space on the output volume before a run |

The reference inputs are generated from the original CPU seed generator. No
external dataset, employer source, Unity project, or private asset is needed.
Two cohorts, four cases and 144 frames/cohort/case produce about 597 MB of CPU
reference pixels. Keep about 12 GB for discovery/confirmation raw exports and
additional space for build/dependency caches. Every output directory must be new.

The unchanged upstream RTS shaders are pinned at
`98d93a4e9ed2f3c8353119515bf9be90a2e137ad`, with their MIT license and source lock.
Do not substitute the old native host's DXC version: this managed integration's
actual compiler/runtime hashes are frozen separately.

## Build and correctness

Use a clean checkout on its own `codex/` branch. For a new machine, provision
the dependencies and an explicit campaign/queue path after host authorization.
The current local queue remains in
`D:/CodexWork/whole-task-validation-20260915/coordination`.

```powershell
$env:HLSLPERF_CROWD_ADAPTER = 'NVIDIA GeForce RTX 4090'
$taskDotnet = 'dotnet' # Or the absolute path to the installed 10.0.302 host.
$taskDll = 'tools/HlslPerf.CrowdWholeTask/bin/Release/net10.0/HlslPerf.CrowdWholeTask.dll'
$coordination = 'D:/CodexWork/whole-task-validation-20260915/coordination'

& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage build -OutputDirectory .scratch/build-new -Action {
    & $taskDotnet restore tools/HlslPerf.CrowdWholeTask -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Restore failed' }
    & $taskDotnet build tools/HlslPerf.CrowdWholeTask -c Release --no-restore --disable-build-servers
    if ($LASTEXITCODE) { throw 'Build failed' }
    & $taskDotnet test tests/HlslPerf.Core.Tests -c Release --disable-build-servers -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'CPU tests failed' }
    python tools/test_crowd_analysis.py
    if ($LASTEXITCODE) { throw 'Analysis controls failed' }
    python tools/verify_external_sources.py
    if ($LASTEXITCODE) { throw 'Pinned source check failed' }
    & $taskDotnet $taskDll oracle . .scratch/references-new
    if ($LASTEXITCODE) { throw 'Oracle generation failed' }
}

& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage correctness -OutputDirectory .scratch/correctness-lock-new -Action {
    python tools/run_crowd_whole_task.py snapshot --output .scratch/source-new
    & $taskDotnet $taskDll validate . .scratch/validation-new
    if ($LASTEXITCODE) { throw 'Boundary correctness failed' }
    & $taskDotnet $taskDll check-scenes . .scratch/full-scenes-new .scratch/references-new
    if ($LASTEXITCODE) { throw 'Full scene correctness failed' }
}
```

Cached dependencies can be restored with the repository's
`examples/HlslPerf.PrimitiveApp/NuGet.offline.config`. If `DOTNET_CLI_HOME` is
isolated, explicitly point `NUGET_PACKAGES` to the existing package cache.
Ordinary desktop activity can coexist with non-timed correctness, provided the
queue, mutex and resource budgets pass. The performance idle gate is stricter.

`validate` compares all pixels, stable visible IDs/counts, tile member multisets,
exclusive offsets, cursors, immutable input and capacity guards. `check-scenes`
adds small/medium/large/dense scenes and two independent seed cohorts. Both
advance frame constants on already allocated buffers.

## Complete caller/export rehearsal

This explicitly ineligible mode exercises the same first-use, resource reuse,
GPU-to-CPU transfer, raw-file export, cleanup and receipt code as timing. It
labels samples `rehearsal` and the result `performanceEligible=false`.
Discovery/freeze reject such a result.

```powershell
& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage correctness -OutputDirectory .scratch/rehearsal-lock-new -Action {
    foreach ($arm in @('hierarchical','fused','wave-tiled','rts')) {
        & $taskDotnet $taskDll rehearse . ('.scratch/rehearsal-new/' + $arm) .scratch/references-new discovery-large $arm
        if ($LASTEXITCODE) { throw ('Rehearsal failed: ' + $arm) }
    }
}
```

## Discovery, frozen confirmation and analysis

Performance requires idle snapshots: CPU <=25%, GPU <=15%, sufficient memory
and disk, no known concurrent experiment, and the independent shared mutex.
Keep the debugger and invasive profiler disabled for these samples. Discovery's
per-pass timestamps are taken only after its complete-task samples.

```powershell
& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage performance -OutputDirectory .scratch/performance-lock-new -Action {
    python tools/run_crowd_whole_task.py discover --dotnet $taskDotnet --references .scratch/references-new --output .scratch/discovery-new
    if ($LASTEXITCODE) { throw 'Discovery incomplete' }
}
```

Review the complete-task and stage diagnostics before registration. Keep every
attempt if a repair is needed; changed candidates need new discovery evidence.
Then freeze and confirm the final implementation:

```powershell
& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage performance -OutputDirectory .scratch/confirmation-lock-new -Action {
    python tools/run_crowd_whole_task.py freeze --references .scratch/references-new --validation .scratch/validation-new --full-scenes .scratch/full-scenes-new --discovery .scratch/discovery-new --output .scratch/registration-new
    if ($LASTEXITCODE) { throw 'Registration failed' }
    python tools/run_crowd_whole_task.py confirm --dotnet $taskDotnet --references .scratch/references-new --plan .scratch/registration-new/plan.json --output .scratch/confirmation-new
    if ($LASTEXITCODE) { throw 'Confirmation incomplete; retain the attempt' }
    python tools/run_crowd_whole_task.py analyze --confirmation .scratch/confirmation-new --output .scratch/analysis-new
    if ($LASTEXITCODE) { throw 'Evidence audit failed' }
}
```

There are four discovery cases x four arms and 128 confirmation processes
(eight rounds x four cases x four arms). Four-row Williams orders balance arm
positions and immediate predecessors; case order rotates by round. Each process
has one first use, three warmups and eight separately submitted/completed
twelve-frame requests. Input seed and all 144 frames are shared within each
case/round. Confirmation uses the untouched second seed cohort.

The primary outcome is CPU latency through full RGBA export for the large case.
Report all other cases, first-use latency, all actual request costs and cleanup
over the twelve-request lifetime, GPU rendering/readback, and resource costs.
Intervals use eight process means; inner requests are not independent processes.
The complete twelve-request cost excludes verification gaps and includes actual
resource cleanup; it is accumulated application work, not elapsed wall time
through the experiment's oracle checks. Buffered export is not durable-storage
latency. Presentation, video encoding, and other GPUs remain outside this claim.

New-host validation and registration are mandatory: do not replay a local
device/compiler/binary freeze as if it describes another host. No remote host,
purchase, cache clearing, power-setting change, or unattended queue is created
by these scripts.
