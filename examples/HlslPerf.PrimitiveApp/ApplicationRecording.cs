using HlslPerf.Core;
using HlslPerf.D3D12;
using Vortice.Direct3D12;

namespace HlslPerf.PrimitiveApp;

/// <summary>Compiled application integration; the CPU Main never calls this GPU path.</summary>
public static class ApplicationRecording
{
    /// <summary>
    /// Borrow an OPEN command list, already initialized resources and matching PSOs.
    /// The application owns every argument and serializes their use on this queue.
    /// Return the application's fence ticket; do not reuse/free anything until it completes.
    /// </summary>
    public static ulong RecordAndSubmit(OperationSelection selection,
        ID3D12GraphicsCommandList commands, ID3D12CommandQueue queue,
        ID3D12Fence fence, ulong previousUseFenceValue, ulong nextFenceValue,
        ID3D12RootSignature root,
        IReadOnlyDictionary<string, ID3D12PipelineState> pipelines,
        IReadOnlyDictionary<string, ID3D12Resource> buffers,
        IDictionary<string, ResourceStates> states, ID3D12Resource dummy)
    {
        // Check completion BEFORE the caller resets its allocator or overwrites uploads
        // as well. This second guard does not make unsafe earlier reuse valid.
        if (!CanReuse(fence, previousUseFenceValue))
            throw new InvalidOperationException("The application's previous submission is still in flight.");
        if (nextFenceValue <= previousUseFenceValue || nextFenceValue == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(nextFenceValue), "Supply a fresh, monotonically increasing queue fence value.");

        // Build/upload/compile from selection.Plan, which may be the fallback plan.
        // Root: D3D12OperationRecorder.CreateRootSignature(device).
        // PSOs: all plan.Shaders, with their exact source/options/defines/include graph.
        // Buffers: plan.Buffers, distinct UAV-capable resources, plus a distinct
        // >=256-byte UAV-capable dummy in COMMON. states reflects actual queue state.
        // InitialData uploads must already be recorded/ordered before these passes.
        var recorder = new D3D12OperationRecorder(selection.Plan, root, pipelines, buffers, states, dummy);
        recorder.Record(commands); // Includes restore, initialization, conversion and barriers.

        // An application's downstream GPU consumer/readback can instead be recorded
        // here, with its transitions entered in the same states map, before closing.
        commands.Close();
        queue.ExecuteCommandList(commands);
        queue.Signal(fence, nextFenceValue).CheckError();
        // App accounts for buffer decay at ExecuteCommandLists boundaries before its
        // next recording. An abandoned/failed submission also requires state resync.
        // If Signal fails after Execute, do not interpret the exception as completion:
        // keep allocations alive until the application's queue/device recovery ends.
        return nextFenceValue;
    }

    public static bool CanReuse(ID3D12Fence fence, ulong ticket)
    {
        ulong completed = fence.CompletedValue;
        if (completed == ulong.MaxValue) throw new InvalidOperationException("D3D12 device removed; application recovery required.");
        return completed >= ticket;
    }
}
