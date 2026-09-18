# Real Unity Crowd hero

This is a **new real Unity Player capture**, recorded on September 19, 2026 (Asia/Singapore), using Unity **6000.3.13f1**, D3D11 and an NVIDIA GeForce RTX 4090. It is not a synthetic simulation video or image-generated scene. The repository's `LiveGpuDrivenCrowd.cs`, `.compute` and `.shader` were copied unchanged into a standalone project.

| Output | Dimensions | Duration | Encoding cadence | Bytes |
|---|---|---|---|---:|
| [README GIF](gpu-driven-crowd-hero.gif) | 768 × 432 | 12.01 seconds | ~8 fps | 13,791,113 |
| [Higher-quality MP4](gpu-driven-crowd-hero.mp4) | 960 × 540 | 12 seconds | 24 fps | 10,173,200 |

These are **offline capture/playback cadences, not achieved application FPS**. The GIF repeats a forward-time excerpt and visibly resets at the loop boundary; it does not fake a seamless camera orbit. MP4 preserves smoother motion and more color detail. The smaller GIF balances readability against GitHub download size.

## What is executing

The sample creates **1,000,000 total agents**, culls on GPU, appends visible IDs, copies the GPU counter to indirect arguments and draws from the compacted set. Only the culled subset is rendered. The overlay uses the sample's real low-frequency **asynchronous visible-count telemetry**; it can lag the recorded frame and never determines rendering. Recorded one-second samples are approximately 4,400–4,500 visible agents; see [runtime telemetry](capture-runtime.txt).

The capture helper adds a dark header/footer and renders the actual camera to a 960 × 540 RenderTexture, then reads pixels for recording. It neither generates a CPU visibility list nor fabricates agent imagery. This extra capture readback and manual camera render are presentation overhead, not part of the benchmark. No new RenderDoc capture was made: the separately linked September 17 capture establishes the command/resource chain.

Presentation-only settings: view half-extents `(8, 4.5)`, orbit radius `20`, speed `0.06`, agent size range `0.025–0.07`; world half-extents `(120, 68)` and seed `69501203` are unchanged. The smaller window makes individual agents and their entry/exit legible instead of drawing a dense full-screen cloud. No compute or draw shader changes.

It proves a real repository GPU-driven sample executes and produces the visualized output, complementing the existing GPU culling/compaction/draw-count architecture evidence. It does **not** prove CPU-vs-GPU speedup, one million simultaneously rendered agents, controlled frame-rate scalability, or the absence of capture overhead.

## Reproduce the real capture

Requires Windows, Unity 6000.3.13f1 with Windows Player support and an active license, compute-capable GPU, Unity UGUI 2.0.0, and FFmpeg. From the repository root:

```powershell
& tools/Invoke-PortfolioCrowdCapture.ps1 `
  -Unity 'C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Unity.exe' `
  -Project 'D:/CodexValidation/new-portfolio-capture'
```

Use a fresh project path. The script copies the unchanged sample and the two checked-in `tools/portfolio-unity/` helpers, builds a Player, and runs it with a hidden window. After three seconds of simulated warmup, the camera captures **288 real frames**. `frames/complete.txt` must exist; verify images are nonblack and the telemetry changes before encoding. This is a scripted camera capture, not GUI automation.

Initial hidden-window screen capture produced black images despite live GPU telemetry. Those failed frames remain locally; the delivered frames use explicit offscreen camera rendering. A first successful offscreen composition was too dense and is also retained locally. Neither attempt is presented as performance evidence.

Encode from the successful frame directory (set `$frames` to its absolute path):

```powershell
$frames = 'D:/CodexValidation/hlsl-portfolio-capture-20260919/frames-hero'
ffmpeg -framerate 24 -i "$frames/frame-%04d.png" -c:v libx264 -crf 18 -preset slow -pix_fmt yuv420p -movflags +faststart docs/media/gpu-driven-crowd-hero.mp4
ffmpeg -i docs/media/gpu-driven-crowd-hero.mp4 -filter_complex '[0:v]fps=8,scale=768:432:flags=lanczos,split[a][b];[a]palettegen=max_colors=64:stats_mode=diff[p];[b][p]paletteuse=dither=none' -loop 0 docs/media/gpu-driven-crowd-hero.gif
```

FFmpeg 8.1.1 was used. No frame interpolation, composited fake agents or manufactured telemetry. Encoding can be repeated from the raw sequence; timing and visual workload must not be mistaken for controlled performance measurements.

## Provenance

[provenance.json](provenance.json) records the sample's source commit, hardware, Unity version, presentation-source hashes, exact encoding arguments, local raw paths and final-media hashes. [raw-frames.json](raw-frames.json) records all 288 raw-frame sizes and SHA256 values. Original frame sequence, Player, build logs and failed attempts remain under `D:/CodexValidation/hlsl-portfolio-capture-20260919/`; that path is local, not a public download. Public GIF/MP4 and telemetry are committed; raw lossless frames are retained locally.

The [September 17 architecture validation](../results/LIVE_GPU_DRIVEN_VALIDATION_2026-09-17.md) and [RenderDoc evidence](../evidence/live-gpu-crowd-20260917/README.md) remain separate, unchanged sources. This new visual does not update their historical measured conditions.
