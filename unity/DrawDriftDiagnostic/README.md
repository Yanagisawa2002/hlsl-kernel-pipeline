# Isolated draw-drift diagnostic

This toolchain builds a diagnostic derivative, never a replacement acceptance Player. Read [PROTOCOL.md](PROTOCOL.md) before any hardware run. Current task already executed its four permitted processes; these commands document reproduction, not authorization to repeat them.

```powershell
python tools/prepare_draw_drift.py --project D:\DiagnosticProject --native-output D:\DiagnosticNative --unity 'C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor'
$env:CROSSOVER_PLAYER_PATH='D:\DiagnosticPlayer\DrawDrift.exe'
$env:CROSSOVER_AUTOCONNECT='0'
# Run installed Unity in batch mode with this project and executeMethod HlslPerf.Crossover.BuildCrossover.Build.
# Use a fresh project/output; never overwrite the acceptance project or Player.
python tools/run_draw_drift.py --player D:\DiagnosticPlayer\DrawDrift.exe --output D:\DiagnosticRuns --calibration <original-1000-view-calibration.json> --stage short
# Only if short instrumentation validation succeeds, fixed order, one invocation each:
python tools/run_draw_drift.py --player D:\DiagnosticPlayer\DrawDrift.exe --output D:\DiagnosticRuns --calibration <same-calibration.json> --stage forward
python tools/run_draw_drift.py --player D:\DiagnosticPlayer\DrawDrift.exe --output D:\DiagnosticRuns --calibration <same-calibration.json> --stage frozen
python tools/run_draw_drift.py --player D:\DiagnosticPlayer\DrawDrift.exe --output D:\DiagnosticRuns --calibration <same-calibration.json> --stage reverse
```

Preparation needs Windows MSVC x64 and installed Unity PluginAPI. It exports the unchanged model/shaders/build settings and patches only the isolated diagnostic project/native derivative. It refuses existing output directories. The actual Draw method is regression-checked unchanged. Pipeline statistics and 18-word native return payload exist only in that derivative.

Each diagnostic launch requires a new receipt path and completed preceding stages, starts a persistent anonymous NVML sidecar before the Player, and stops it after exit. A failed receipt/result is retained. No utilization-driven retry or acceptance promotion exists. Optional NVML fields are null with return codes; unavailable NVML initialization stops before Player launch. 100 ms cadence is fixed.

Offline analysis works on local raw files or the committed lossless gzip files:

```powershell
python tools/analyze_draw_drift.py --directory docs/evidence/draw-drift-diagnostic-20260919 --output D:\NewDiagnosticAnalysis.json
python -m unittest discover -s tools -p test_draw_drift.py
```

Every output is diagnostic-only and ineligible for performance acceptance. Whole-process batch values inherited in raw schema104 are not analyzed or compared. The formal schema4 checker rejects these results. No change to the v4 threshold, timestamp code, runner or fairness rules.
