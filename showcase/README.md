# Scan particle visual showcase

This standalone Windows/D3D12 showcase turns the scan primitive into an actual
GPU-generated particle-field image. It is not part of the Unity package and does
not add an engine dependency to `HlslPerf.Core` or the workload pack.

## What runs

Each candidate performs one or more complete global exclusive scans over a
deterministic flag buffer, then `VisualizeScan` reads the prefix data and emits a
24-frame 480x270 RGBA atlas. Four manifests cover low, medium, high, and extreme
pressure. Large workloads flatten group ids across two dispatch dimensions so
all candidate group sizes remain valid past D3D12's 65,535 single-dimension
limit.

The D3D12 backend's optional verified-output callback captures the exact GPU
readback bytes. The showcase refuses to compose an A/B if baseline and tuned
atlases differ. It records the two candidates sequentially, never concurrently.

## Run

Requirements are the same as the main pipeline plus `ffmpeg` on `PATH`:

    dotnet build HlslKernelPipeline.slnx -c Release
    dotnet run --project src/HlslPerf.Showcase -c Release --no-build

Use `--output <directory>` to choose an ignored evidence directory. Each level
writes the normal `run.json`, candidate CSV, report, selected profile, retained
raw GPU captures, PNG frames, GIF, and MP4. A root summary identifies the largest
guarded speedup.

GIF playback is timestamp-paced: the phase advance reflects measured median GPU
time so small throughput differences can be seen without changing the source
frames. The overlay states that upload, readback, and CPU composition are outside
the GPU timestamp interval.
