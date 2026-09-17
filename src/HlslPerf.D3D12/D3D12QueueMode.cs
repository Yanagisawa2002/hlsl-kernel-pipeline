using HlslPerf.Core;
using Vortice.Direct3D12;

namespace HlslPerf.D3D12;

/// <summary>
/// Selects the command queue used by a D3D12Tuner instance. Existing callers keep
/// the compute-only queue by default; live presentation explicitly opts into a
/// direct queue, which can execute the same compute workloads and present them.
/// </summary>
public enum D3D12QueueMode
{
    Compute,
    Direct
}

public sealed partial class D3D12Tuner
{
    private CommandListType unifiedQueueType = CommandListType.Compute;

    /// <summary>
    /// Creates a tuner on an explicit queue type. Use the existing string-only
    /// constructor for ordinary benchmark work; this overload exists so a live
    /// visual caller can keep compute and presentation on one ordered D3D12 queue.
    /// </summary>
    public D3D12Tuner(D3D12QueueMode queueMode, string? adapterNameContains = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The HlslPerf D3D12 backend requires Windows.");

        unifiedQueueType = queueMode switch
        {
            D3D12QueueMode.Compute => CommandListType.Compute,
            D3D12QueueMode.Direct => CommandListType.Direct,
            _ => throw new ArgumentOutOfRangeException(nameof(queueMode))
        };

        (device, adapterDescription, driverVersion) = CreateDevice(adapterNameContains);
        queue = device.CreateCommandQueue(
            unifiedQueueType,
            CommandQueuePriority.Normal,
            CommandQueueFlags.None,
            0);
        allocator = device.CreateCommandAllocator(unifiedQueueType);
        commandList = device.CreateCommandList<ID3D12GraphicsCommandList>(
            unifiedQueueType,
            allocator,
            null!);
        fence = device.CreateFence(0, FenceFlags.None);

        Result frequencyResult = queue.GetTimestampFrequency(out ulong frequency);
        if (frequencyResult.Failure || frequency == 0)
            throw new InvalidOperationException($"The D3D12 queue did not expose a timestamp frequency ({frequencyResult}).");
        timestampFrequency = frequency;

        RootParameter1[] parameters =
        [
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0, RootDescriptorFlags.None), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0, RootDescriptorFlags.None), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0, RootDescriptorFlags.None), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0, RootDescriptorFlags.None), ShaderVisibility.All),
            new(new RootConstants(0, 0, KernelAbiV1.RootConstantCount), ShaderVisibility.All)
        ];
        rootSignature = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters, []));

        timestampQueryHeap = device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, 2, 0));
        timestampReadback = device.CreateCommittedResource(
            HeapType.Readback,
            ResourceDescription.Buffer(2 * sizeof(ulong), ResourceFlags.None, 0),
            ResourceStates.CopyDest,
            null);
    }
}
