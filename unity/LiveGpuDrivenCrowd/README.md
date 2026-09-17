# Live GPU-Driven Crowd (Unity presentation sample)

This sample is the visual proof that the offline Crowd/VFX composer cannot provide.
Its render path stays GPU-resident:

`static agent buffer -> compute cull -> AppendStructuredBuffer compacted IDs -> CopyCount into indirect args -> DrawProceduralIndirect`

There is no CPU visibility list, CPU instance-count decision, per-frame agent upload or RGBA readback in the rendering path. A low-frequency `AsyncGPUReadback` of the four-uint indirect-argument buffer is used only for the on-screen visible-count label and never feeds rendering.

## Setup

1. Create or open a Unity project with compute-shader support (D3D11/D3D12, Vulkan or another backend supporting the required features).
2. Copy this directory under the project's `Assets/` folder.
3. Create an empty GameObject and attach `LiveGpuDrivenCrowd.cs`.
4. Assign `LiveGpuDrivenCrowd.compute` to **Culling Shader**.
5. Assign shader `HlslPerf/LiveGpuDrivenCrowd` to **Draw Shader**.
6. Enter Play Mode.

The default scene creates 160,000 agents once, moves the logical camera window continuously, compacts only visible agent indices on GPU, copies the append counter directly into the indirect draw's `instanceCount`, and draws six vertices per visible agent.

## What to record for the portfolio clip

Capture 15-20 seconds showing the moving view with the overlay visible. Then record one RenderDoc/PIX frame if available and highlight:

- `CullAgents` compute dispatch;
- append/compact visible-ID buffer;
- GPU-written indirect argument buffer;
- indirect procedural draw;
- no CPU readback dependency in the render path.

This Unity sample is deliberately separate from the native complete-task benchmark. It demonstrates a different **GPU-resident presentation contract**. Do not use its frame rate to reinterpret the September 16 CPU-input/CPU-output Crowd benchmark; benchmark this path independently if performance claims are needed.
