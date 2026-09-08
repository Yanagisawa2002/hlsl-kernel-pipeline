using HlslPerf.Core;
using Vortice.Direct3D12;

namespace HlslPerf.D3D12;

/// <summary>Borrowed-buffer SDK recorder. The application owns PSOs, allocations, upload,
/// command-list submission, fences and disposal. No tuner, timestamps or readback are involved.</summary>
public sealed class D3D12OperationRecorder
{
    private readonly UnifiedOperationPlan plan;
    private readonly ID3D12RootSignature root;
    private readonly IReadOnlyDictionary<string, ID3D12PipelineState> pipelines;
    private readonly IReadOnlyDictionary<string, ID3D12Resource> buffers;
    private readonly IDictionary<string, ResourceStates> states;
    private readonly ID3D12Resource dummy;
    private readonly Dictionary<string, uint[]> constants;

    public D3D12OperationRecorder(UnifiedOperationPlan plan, ID3D12RootSignature root,
        IReadOnlyDictionary<string, ID3D12PipelineState> pipelines,
        IReadOnlyDictionary<string, ID3D12Resource> buffers,
        IDictionary<string, ResourceStates> states, ID3D12Resource dummy)
    {
        plan.Validate();
        this.plan = plan; this.root = root; this.pipelines = pipelines;
        this.buffers = buffers; this.states = states; this.dummy = dummy;
        foreach (var spec in plan.Buffers)
            if (!buffers.TryGetValue(spec.Name, out var buffer) || buffer.Description.Dimension != ResourceDimension.Buffer ||
                buffer.Description.Width < (ulong)spec.ByteLength || !states.ContainsKey(spec.Name) ||
                (buffer.Description.Flags & ResourceFlags.AllowUnorderedAccess) == 0)
                throw new InvalidDataException("Missing, undersized or non-UAV resource/state: " + spec.Name);
        if (plan.Buffers.Select(b => buffers[b.Name].NativePointer).Append(dummy.NativePointer).Distinct().Count() != plan.Buffers.Count + 1)
            throw new InvalidDataException("Operation buffer names and dummy must refer to distinct resources.");
        if (dummy.Description.Dimension != ResourceDimension.Buffer || dummy.Description.Width < 256 ||
            (dummy.Description.Flags & ResourceFlags.AllowUnorderedAccess) == 0)
            throw new InvalidDataException("Dummy requires at least 256 bytes, UAV capability and COMMON state at recording.");
        foreach (var shader in plan.Shaders)
            if (!pipelines.ContainsKey(shader.Id)) throw new InvalidDataException("Missing compiled pipeline: " + shader.Id);
        constants = plan.Passes.Where(p => p.ShaderId is not null).ToDictionary(p => p.Name, p =>
        {
            var values = new uint[8];
            for (int i = 0; i < p.Constants.Count; i++) values[i] = p.Constants[i];
            return values;
        });
    }

    public static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        List<RootParameter1> parameters =
        [
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
            new(new RootConstants(0, 0, 8), ShaderVisibility.All)
        ];
        for (uint slot = 2; slot < 5; slot++)
            parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(slot, 0), ShaderVisibility.All));
        return device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters.ToArray(), []));
    }

    /// <summary>Caller has uploaded InitialData once and serialized all uses of these resources.
    /// States are updated to match the recorded commands; abandoned command lists require resynchronization.</summary>
    public void Record(ID3D12GraphicsCommandList commands)
    {
        void Transition(string name, ResourceStates target)
        {
            ResourceStates previous = states[name];
            if (previous == target) return;
            commands.ResourceBarrierTransition(buffers[name], previous, target);
            states[name] = target;
        }
        foreach (var pass in plan.Passes)
        {
            if (pass.CopySource is { } source)
            {
                Transition(source, ResourceStates.CopySource);
                Transition(pass.CopyDestination!, ResourceStates.CopyDest);
                commands.CopyBufferRegion(buffers[pass.CopyDestination!], 0, buffers[source], 0, (ulong)pass.CopyBytes);
                continue;
            }
            foreach (var input in pass.Srvs) if (input is not null) Transition(input, ResourceStates.NonPixelShaderResource);
            foreach (var output in pass.Uavs) if (output is not null) Transition(output, ResourceStates.UnorderedAccess);
            commands.SetComputeRootSignature(root);
            commands.SetPipelineState(pipelines[pass.ShaderId!]);
            for (uint i = 0; i < 2; i++)
                commands.SetComputeRootShaderResourceView(i,
                    i < pass.Srvs.Count && pass.Srvs[(int)i] is { } input ? buffers[input].GPUVirtualAddress : dummy.GPUVirtualAddress);
            for (uint i = 0; i < 5; i++)
                commands.SetComputeRootUnorderedAccessView(i < 2 ? i + 2 : i + 3,
                    i < pass.Uavs.Count && pass.Uavs[(int)i] is { } output ? buffers[output].GPUVirtualAddress : dummy.GPUVirtualAddress);
            commands.SetComputeRoot32BitConstants(4, constants[pass.Name], 0);
            commands.Dispatch(pass.Dispatch!.X, pass.Dispatch.Y, pass.Dispatch.Z);
            commands.ResourceBarrierUnorderedAccessView(null!);
        }
    }
}
