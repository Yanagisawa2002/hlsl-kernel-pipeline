# RGA evidence adapter

The optional adapter targets AMD Radeon GPU Analyzer's DirectX 12 live-driver
mode. AMD documents that this mode compiles a compute pipeline through the live
driver and can emit ISA, VGPR/SGPR/LDS resource statistics, and live-register
analysis:

- [RGA product and current releases](https://gpuopen.com/rga/)
- [Direct3D 12 compute workflow](https://gpuopen.com/learn/radeon-gpu-analyzer-2-2-direct3d12-compute/)
- [Live VGPR analysis](https://gpuopen.com/learn/live-vgpr-analysis-radeon-gpu-analyzer/)
- [Occupancy explained](https://gpuopen.com/learn/occupancy-explained/)

The tested command shape is:

    rga -s dx12 --cs kernel.hlsl --cs-entry Entry --cs-model cs_6_0 \
      --analysis stats.txt --isa isa.txt --livereg livereg.txt \
      --asic gfx1201 --define NAME=VALUE

The collector accepts both the older live summary (`# VGPR allocated`) and the
RGA 2.14.2 summary (`VGPRs allocated by HW`). It also accepts target-prefixed
output names such as `gfx1201_Entry-stats_comp.txt`.

## Evidence semantics

- `numUsedVgprs`/`numUsedSgprs` are compiler resource requirements.
- maximum live VGPRs are a liveness-analysis lower bound.
- allocated VGPRs include hardware allocation granularity.
- LDS and scratch are stored as bytes.
- measurements and RGA evidence are joined by candidate define identity and
  entry point.

RGA 2.14.2 DX12 statistics do not emit a complete theoretical occupancy value.
Register-limited arithmetic alone would ignore wave mode, allocation granularity,
LDS, thread-group placement, and architectural limits. Therefore
`occupancyWavesPerSimd` remains nullable unless an analyzer explicitly reports
it. A future RGP/runtime-counter adapter can fill the missing evidence without
changing the ABI or Unity consumer.

RGA is not redistributed. `--rga auto` discovers an existing install, `--rga
off` disables it, and `--rga <path>` uses an explicit executable.

## vNext complete entrypoint receipts

`RgaCollector.AnalyzeCandidate` is also available to evidence runners without
inventing a tuning report. It visits every distinct plan entrypoint, including
`ResetSinglePassState`, `SinglePassScan` and `FusedCompactSinglePass`. Each attempt
gets a new directory so stale files cannot satisfy a failed collection. Failed
entries remain present with `status: unavailable`; candidate status is `partial`
when only some entries succeed.

Each entry stores the full process output, exact argument vector and exit code,
statistics/ISA/live-register output and pipeline binary where supported. Its
`invocation.json` hashes the executable, explicitly selected bundled DXC package,
raw artifacts and HLSL source graph. Source snapshots are retained alongside the
receipt. The measured device/driver identity is attached separately. RGA uses
`-O3 -Ges -WX` and source include directories. This is an identified source
recompilation; it is not claimed to be byte-identical to the runner's DXIL. The
runner independently records hashes of its actual DXIL and loaded native DXC.

VGPR/SGPR spill counts are null unless explicit `resourceUsage.numVgprSpills` or
`resourceUsage.numSgprSpills` fields exist. Scratch bytes do not imply a spill
count. Invalid/negative/nonfinite metrics remain null. Generic `occupancy` has
ambiguous units and is not accepted as waves/SIMD; an explicitly named
`occupancyWavesPerSimd` field, if present, is still static analyzer data. No RGA
resource field is runtime measured occupancy or bandwidth.

## Bounded hardware smoke

Run under the shared project coordinator lock (paths below are arguments, not
global environment changes):

```powershell
./tools/Invoke-BoundedHardwareEvidence.ps1 `
  -ValidationLockScript '<control>/Invoke-SerializedValidation.ps1' `
  -RgaPath '<existing-rga-install>/rga.exe' `
  -OutputDirectory '<new-evidence-directory>'
```

The predeclared `manifests/evidence/bounded-r9700.json` matrix has five cells:
cache-warm scan, three-slot scan and fused compaction with changing seeds, and
larger 4,194,307-item scan/compaction. It preserves partial-block boundaries and
caps each session at 512 MiB. Three batches of twelve complete plans provide
smoke timestamps only. They do not replace the coordinator's paired calibration
and independent confirmation. The output directory must be new, every failed
attempt is retained, and RGA has a two-minute per-entrypoint timeout.
