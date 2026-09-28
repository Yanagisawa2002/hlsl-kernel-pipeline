# hlsl-kernel-pipeline TODO

Updated: 2026-09-28. Repository-specific handoff for testing on another device. All tasks below are pending on that device; this documentation change runs no benchmarks or application tests.

Source baseline: `cfcd8443d1c21f4df370639ea2702972dd9c4313` on main. Start with the [pinned README](https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/blob/cfcd8443d1c21f4df370639ea2702972dd9c4313/README.md) for prerequisites and the scope of historical evidence.

## Prepare the checkout

- [ ] Clone into a fresh directory and select the recorded source commit. If deliberately using a newer revision, record the new SHA and review the intervening changes.

```powershell
gh repo clone Yanagisawa2002/hlsl-kernel-pipeline
Set-Location hlsl-kernel-pipeline
git switch --detach cfcd8443d1c21f4df370639ea2702972dd9c4313
git rev-parse HEAD
```

## Functional checks first

- [ ] Install .NET SDK 10.0.302 as pinned by `global.json`. Run the README's CPU plan/check path:

```powershell
dotnet restore examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj --configfile examples/HlslPerf.PrimitiveApp/NuGet.offline.config -p:NuGetAudit=false
dotnet build examples/HlslPerf.PrimitiveApp/HlslPerf.PrimitiveApp.csproj -c Release --no-restore
dotnet run --project examples/HlslPerf.PrimitiveApp -c Release --no-build --no-restore -- . --check
```

- [ ] For actual GPU execution, prepare a supported Windows D3D12 GPU and record compiler/device/driver identity. Keep CPU planning separate from native execution acceptance.
- [ ] Exercise correctness and resource ownership for the selected primitive/consumer before calibration; retain complete output validation, not only timing samples.
- [ ] If testing the Unity GPU-driven crowd, follow `unity/LiveGpuDrivenCrowd/README.md` and verify culling, compact/append IDs, indirect arguments and indirect rendering on the new device.
- [ ] Keep the native preview's readback presentation separate from the Unity GPU-resident path.
- [ ] For later profiling, freeze workload/binary/input identity and follow the paired-process protocol. Profile markers must identify the actual process and queue range; missing counters remain unavailable.
- [ ] Preserve negative CPU/GPU comparisons and upstream attribution. Historical RTX 4090 measurements are not new-machine results or a general application speedup.

## Record the new-device outcome

- [ ] Record source and binary identity, machine/OS, toolchain, hardware/driver, command/exit code, input/configuration hashes and the actual PASS / FAIL / BLOCKED / NOT RUN result.
- [ ] Keep generated binaries, captures and large assets outside source control. Link the retained evidence from the affected validation document/PR.
- [ ] Preserve unavailable metrics and negative outcomes. Separate static/functional success, runtime correctness and performance conclusions.

Create a focused `codex/` branch before implementing a fix. Existing unexecuted acceptance items remain open until the selected workflow is actually run.
