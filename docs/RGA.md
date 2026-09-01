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
