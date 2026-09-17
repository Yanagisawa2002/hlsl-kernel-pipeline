# Live GPU-driven crowd

This sample exists to demonstrate a genuinely live GPU-driven rendering path, not the repository's offline atlas-to-media visualization.

Per frame:

1. An immutable GPU `StructuredBuffer<float4>` holds the full agent set.
2. `LiveGpuDrivenCrowd.compute` frustum-culls agents on the GPU.
3. Visible agents are compacted into an `AppendStructuredBuffer<float4>`.
4. `ComputeBuffer.CopyCount` copies the GPU append counter directly into the indexed indirect argument buffer's `instanceCount` field.
5. `Graphics.DrawMeshInstancedIndirect` consumes the compacted buffer and GPU-generated instance count.
6. No full visible-agent buffer or rendered atlas is read back for submission or presentation.

The optional overlay performs a tiny asynchronous readback of the five-uint indirect argument buffer at low frequency so a human can see the delayed visible count. Disable `showOverlay` to remove even that diagnostic readback.

## Run

Use Unity 2022.3 or later with a D3D11/D3D12-capable desktop GPU.

1. Import the `HLSL Performance Profile Consumer` package and its **Live GPU-driven Crowd** sample.
2. Create an empty GameObject and add `LiveGpuDrivenCrowd`.
3. Assign `LiveGpuDrivenCrowd.compute` and shader `HlslPerf/LiveGpuDrivenCrowd`.
4. Assign a Camera, or tag the active camera `MainCamera`.
5. Optionally add `LiveGpuDrivenCamera` to the camera. RMB looks; WASD moves; Q/E move vertically; Shift accelerates.
6. Enter Play Mode. Start with 1,048,576 agents, then test 2–4 million if VRAM and dispatch limits permit.

The component creates its billboard mesh, material and GPU buffers at runtime, so no scene or prefab asset is required.

## What to capture for the portfolio

Record a 15–30 second clip with the overlay visible while flying the camera through the field. Capture one RenderDoc/PIX frame showing the sequence `CullAgents -> CopyCount -> DrawMeshInstancedIndirect` and the indirect instance count. For performance claims, use Unity Profiler/RenderDoc/PIX GPU timing rather than the overlay's CPU frame time.

## Scope

This sample answers a different question from the September 16 complete Crowd/VFX benchmark. That benchmark intentionally measures a CPU-input → full RGBA readback → buffered-export contract. This sample keeps the working set GPU-resident and presents directly, so it is evidence of a live GPU-driven architecture, not a replacement measurement for the complete-task benchmark.
