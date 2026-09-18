# Live GPU-driven Crowd hardware validation — 2026-09-17

## Scope

This validates the live implementations merged at `715dd13b0aef9049095e6530be213bf18bf23986` (`715dd13`) on an NVIDIA GeForce RTX 4090, Windows 11. It separates two workloads:

1. **Native D3D12 live preview:** fused and wave-tiled, with GPU output read back for WinForms presentation.
2. **Unity GPU-resident indirect-render workload:** Unity 6000.3.13f1 using Direct3D11, with visibility and draw-count decisions on the GPU.

The [evidence package](../evidence/live-gpu-crowd-20260917/README.md) records the September 17 validation; this documentation update does not claim a new hardware run of later main commits.

## Native live validation

Both fused and wave-tiled ran successfully at 1,048,576 agents after installation of the repository-pinned .NET SDK **10.0.302**. The first attempts failed because that SDK was absent (PATH provided 10.0.201); this was an environment prerequisite, not a code defect. The preserved native logs describe those initial failures, while the screenshots show the successful reruns.

**Instantaneous live validation samples, not controlled benchmark estimates:**

| Arm | GPU render | GPU readback | CPU fence | Present FPS |
| --- | ---: | ---: | ---: | ---: |
| fused | 0.048 ms | 0.024 ms | 0.256 ms | 63.3 |
| wave-tiled | 0.065 ms | 0.024 ms | 0.341 ms | 64.1 |

Sources: [fused screenshot](../evidence/live-gpu-crowd-20260917/native-fused.png), [wave-tiled screenshot](../evidence/live-gpu-crowd-20260917/native-wave-tiled.png). These single screenshot readings are not averages and support no fused-versus-wave speedup claim. Unity was also running. They are not directly comparable with the earlier 8,388,608-agent × 12-frame complete-task benchmark.

## Unity validation

The [Unity sample](../../unity/LiveGpuDrivenCrowd/README.md) ran and continuously animated successfully at **160,000** and **1,000,000** total agents. The validation harness copied the sample into a standalone Unity project, assigned its shaders, entered Play Mode and changed the agent count by disabling/re-enabling the component. Runtime component source hashes were recorded as matching the validated repository source in the local validation notes.

[Runtime samples](../evidence/live-gpu-crowd-20260917/unity-runtime.txt) show changing view positions over approximately three seconds at each count. The [160k screenshot](../evidence/live-gpu-crowd-20260917/unity-160000.png) reports 11,368 visible; the [1M screenshot](../evidence/live-gpu-crowd-20260917/unity-1000000.png) reports 71,402 visible via asynchronous telemetry. One million total agents does not mean one million simultaneously visible/rendered agents. Two successful runs establish functionality, not performance scalability.

## RenderDoc architecture proof

A RenderDoc 1.46 capture of the 160,000-agent Unity run verified the actual GPU command/data path:

```text
CullAgents
    ↓
AppendStructuredBuffer visible IDs
    ↓
CopyStructureCount
    ↓
GPU-written indirect args
    ↓
DrawInstancedIndirect(6, 11429)
    ↓
draw shader consumes GPU-produced visible-ID buffer
```

The [pipeline extraction](../evidence/live-gpu-crowd-20260917/pipeline-evidence.txt), [captured pipeline screenshot](../evidence/live-gpu-crowd-20260917/renderdoc-gpu-chain.png) and compiled shader evidence connect the same resources across these stages:

| Event | Verified operation and resource identity |
| --- | --- |
| EID 21 | `CullAgents` dispatch `(625, 1, 1)` reads agents buffer `10418` and writes visible-ID UAV buffer `10421`. The append counter is 11,429. |
| Within EID 21 | [Cull shader disassembly](../evidence/live-gpu-crowd-20260917/cull-shader.txt) executes `imm_atomic_alloc` and `store_structured`: append/compaction occurs inside the compute dispatch, not as a separate API event. |
| EID 24 | `CopyStructureCount` copies from UAV view `10422` into args buffer `10424` at byte offset 4; resulting arguments are `(6, 11429, 0, 0)`. |
| EID 852 | `DrawInstancedIndirect` reads the same args buffer `10424` at offset 0: six vertices per instance, 11,429 instances. The vertex shader binds agents `10418` and the same GPU-produced visible-ID buffer `10421`. |

The [draw shader disassembly](../evidence/live-gpu-crowd-20260917/draw-shader.txt) indexes the compacted-ID buffer using instance ID, then loads the corresponding agent. In [the Unity source](../../unity/LiveGpuDrivenCrowd/LiveGpuDrivenCrowd.cs), the CPU resets arguments to `(6, 0, 0, 0)`; `ComputeBuffer.CopyCount` supplies the nonzero instance count, and `Graphics.DrawProceduralIndirect` consumes it (observed as `DrawInstancedIndirect` on D3D11). Low-frequency `AsyncGPUReadback` feeds only the overlay telemetry, not rendering decisions.

**Positive architecture-validation result:** the GPU performs visibility culling, produces the compacted visible set, supplies the draw instance count and renders from those results without a CPU visibility list or CPU visible-count readback to determine the draw count. The intended GPU-driven path actually executes on hardware.

**Claim boundary:** this does not establish that the path is faster than CPU12, does not reinterpret the September 16 complete-task benchmark, and uses a different GPU-resident workload contract. The earlier CPU12 conclusion remains unchanged.

## Validation limitations

- Native screenshot timings are instantaneous and include an uncontrolled desktop environment; they are not controlled performance estimates.
- Unity Editor overhead exists; these runs are not standalone Player performance measurements.
- One Unity Editor Search internal `ArgumentOutOfRangeException` was observed in the [Editor log](../evidence/live-gpu-crowd-20260917/logs/unity-editor.log); it did not prevent either successful run. No shader compile errors were found in that log.
- Static screenshots plus runtime samples document the observed animation; no continuous video is included. Asynchronous overlay counts need not equal a captured frame's count.
- The 8.47 MiB original `.rdc` was retained locally, not committed; the evidence index records its size and SHA-256. Independent replay requires that original file.
- A performance comparison requires a new controlled benchmark with matched workload/output contracts, repeatable timing and explicit synchronization/presentation accounting.
