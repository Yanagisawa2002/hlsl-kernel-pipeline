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
reference pixels. Budget raw exports only after registering the new protocol;
reserve additional space for build/dependency caches. Every output directory must be new.

The unchanged upstream RTS shaders are pinned at
`98d93a4e9ed2f3c8353119515bf9be90a2e137ad`, with their MIT license and source lock.
Do not substitute the old native host's DXC version: this managed integration's
actual compiler/runtime hashes are frozen separately.

## Build and correctness

Use a clean checkout on its own `codex/` branch. For a new machine, provision
the dependencies and an explicit campaign/queue path after host authorization.
The current local queue remains in
`D:/CodexWork/whole-task-validation-20260915/coordination`.

Commit intended source and documentation changes before sealing a build. The
wrapper rejects a dirty checkout or a receipt from another commit. Every check
uses the **same** clean build and its exact DLLs, native dependencies and host.
Do not rebuild or commit between these checks; changed source needs a new build.

```powershell
$env:HLSLPERF_CROWD_ADAPTER = 'NVIDIA GeForce RTX 4090'
$taskDotnet = 'dotnet' # Or the absolute path to the installed 10.0.302 host.
$coordination = 'D:/CodexWork/whole-task-validation-20260915/coordination'
$taskBuildReceipt = '.scratch/build-new/build-receipt.json'

& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage build -OutputDirectory .scratch/build-lock-new -Action {
    python tools/crowd_build_evidence.py build --dotnet $taskDotnet --output .scratch/build-new
    if ($LASTEXITCODE) { throw 'Clean rebuild failed; retain build receipts' }
    python tools/crowd_build_evidence.py check --check cpu-tests --dotnet $taskDotnet --build-receipt $taskBuildReceipt --output .scratch/cpu-new
    if ($LASTEXITCODE) { throw 'CPU tests failed' }
    python tools/test_crowd_build_evidence.py
    if ($LASTEXITCODE) { throw 'Build/debug/protocol controls failed' }
    python tools/test_crowd_analysis.py
    if ($LASTEXITCODE) { throw 'Legacy descriptive analysis controls failed' }
    python tools/verify_external_sources.py
    if ($LASTEXITCODE) { throw 'Pinned source check failed' }
    python tools/crowd_build_evidence.py check --check oracle --dotnet $taskDotnet --build-receipt $taskBuildReceipt --output .scratch/references-new
    if ($LASTEXITCODE) { throw 'Oracle generation failed' }
}

& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage correctness -OutputDirectory .scratch/correctness-lock-new -Action {
    foreach ($mode in @('debug-control','validate','check-scenes')) {
        python tools/crowd_build_evidence.py check --check $mode --dotnet $taskDotnet --build-receipt $taskBuildReceipt --references .scratch/references-new/result --output ('.scratch/checked-new/' + $mode)
        if ($LASTEXITCODE) { throw ('Bound correctness check failed: ' + $mode) }
    }
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

This explicitly ineligible mode exercises first-use, resource reuse, GPU-to-CPU
transfer, buffered raw-file export and cleanup. It labels samples `rehearsal`
and the result `performanceEligible=false`. It is not performance evidence.

```powershell
& tools/Invoke-CrowdWholeTaskLocked.ps1 -Coordination $coordination -Stage correctness -OutputDirectory .scratch/rehearsal-lock-new -Action {
    foreach ($arm in @('hierarchical','fused','wave-tiled','rts')) {
        python tools/crowd_build_evidence.py check --check rehearse --dotnet $taskDotnet --build-receipt $taskBuildReceipt --references .scratch/references-new/result --case discovery-large --arm $arm --output ('.scratch/rehearsal-new/' + $arm)
        if ($LASTEXITCODE) { throw ('Bound rehearsal failed: ' + $arm) }
    }
}
```

Each check directory contains `check-receipt.json`, `process.log`, and `result/`.
The receipt seals source/binary/host identity before and after the child, its
build-receipt hash, PID/command/exit/log and every result artifact. The child
independently records its assembly hash and embedded source commit. Missing,
changed or discarded messages, or filters able to hide warnings/errors, make
correctness ineligible. The device's filters and denied counter are retained;
severity-only Info/Message exclusion is permitted.
`debug-control` deliberately overflows a queue and injects an error on a separate
device to prove rejection; its messages are not workload failures.

## Performance is not ready for registration

The version-1 `discover`, `freeze` and `confirm` commands now reject calls before
creating an output directory or process. **Do not run the old 128-process
matrix.** Its analyzer remains available for descriptive audit and synthetic
regression controls, with `inferentialClaimsEligible=false`.

[Protocol version 2](CROWD_PROTOCOL_V2.md) defines a single primary comparison,
CPU baseline eligibility, process-level uncertainty and how secondary results
must be treated. The conventional CPU implementation, independent CPU/GPU
correctness, discovery and the new registration/confirmation runner are still
required. This change does not assert a performance improvement.

The queue and shared hardware mutex remain mandatory. Non-timed builds and
correctness require their normal CPU/memory/disk budget; performance additionally
requires CPU <=25% and GPU <=15%. No remote host, install, cache clearing, power
change or unattended queue is created by these scripts. Linux/RTX5090 cannot
run this Windows/D3D12 runner directly; no alternate backend is added here.
