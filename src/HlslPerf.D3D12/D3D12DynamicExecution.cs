using HlslPerf.Core;
using Vortice.Direct3D12;
using Vortice.Dxc;

namespace HlslPerf.D3D12;

public sealed partial class D3D12Tuner
{
    private ID3D12PipelineState? indirectPipeline;
    private ID3D12CommandSignature? indirectSignature;

    // This is executor code, not workload code. Raw counts cannot bypass this clamp.
    private const string BoundedArgumentsSource = """
        ByteAddressBuffer Count : register(t0);
        RWByteAddressBuffer Arguments : register(u0);
        cbuffer Params : register(b0) { uint CountOffset; uint ArgumentOffset; uint MaximumItems; uint GroupSize; uint ArgumentWords; uint GroupsX; };
        [numthreads(64,1,1)]
        void BuildBoundedArguments(uint3 tid : SV_DispatchThreadID)
        {
            uint index = tid.x + tid.y * GroupsX * 64;
            if (index >= ArgumentWords) return;
            uint count = min(Count.Load(CountOffset), MaximumItems);
            uint groups = count / GroupSize + ((count % GroupSize) != 0 ? 1 : 0);
            uint first = ArgumentOffset / 4;
            uint value = index == first ? groups : ((index == first + 1 || index == first + 2) ? 1 : 0);
            Arguments.Store(index * 4, value);
        }
        """;

    public static string DynamicExecutorSha256 => ContentHash.Sha256(BoundedArgumentsSource);

    /// <summary>Bounded native correctness smoke. Samples are diagnostics, not selection evidence.</summary>
    public NativePlanValidation ValidateExecutionPlan(KernelExecutionPlan plan, string kernelPath,
        IReadOnlyDictionary<string, int> defines, int repetitions = 3, string shaderModel = "6_0")
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        plan.Validate();
        if (repetitions is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(repetitions));
        HlslSourceGraph source = HlslSourceGraph.Load(kernelPath);
        TuningManifest manifest = new()
        {
            Name = plan.WorkloadId, KernelPath = source.RootPath, KernelAbiVersion = plan.AbiVersion,
            ShaderModel = shaderModel, Axes = []
        };
        CompilationSet compilation = CompilePasses(source.RootSource, source.RootPath, source.CombinedSha256,
            manifest, new KernelCandidate(defines), plan.Passes.Select(pass => pass.EntryPoint).Distinct(),
            null, source.IncludeDirectories);
        if (!compilation.Success) throw new InvalidOperationException(compilation.Diagnostics);
        using PipelineSet pipelines = CreatePipelines(compilation.Bytecodes);
        using ResourceSet resources = CreateResources(plan);
        List<double> samples = [];
        for (int index = 0; index < repetitions; ++index)
            samples.Add(MeasureBatch(plan, pipelines, resources, 1));
        CorrectnessResult correctness = VerifyPlanOutputs(plan, pipelines, resources);
        return new(plan.WorkloadId, plan.AbiVersion, CreateFingerprint(shaderModel), source.CombinedSha256,
            DynamicExecutorSha256, compilation.Bytecodes.ToDictionary(pair => pair.Key, pair => ContentHash.Sha256(pair.Value)),
            plan.Buffers.Sum(buffer => (long)buffer.ByteLength), samples, correctness);
    }

    private void EnsureIndirectPipeline()
    {
        if (indirectPipeline is not null) return;
        using IDxcResult result = DxcCompiler.Compile(DxcShaderStage.Compute, BoundedArgumentsSource,
            "BuildBoundedArguments", new DxcCompilerOptions
            {
                ShaderModel = DxcShaderModel.Model6_0, OptimizationLevel = 3,
                EnableStrictness = true, WarningsAreErrors = true
            });
        if (result.GetStatus().Failure)
            throw new InvalidOperationException($"Bounded indirect argument compilation failed: {result.GetErrors()}");
        indirectSignature = device.CreateCommandSignature<ID3D12CommandSignature>(new CommandSignatureDescription
        {
            ByteStride = KernelIndirectDispatch.ArgumentByteLength,
            IndirectArguments = [new IndirectArgumentDescription { Type = IndirectArgumentType.Dispatch }]
        }, null);
        indirectPipeline = device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = rootSignature, ComputeShader = result.GetObjectBytecodeArray()
        });
    }

    private void PrepareIndirectArguments(KernelIndirectDispatch indirect, ResourceSet resources)
    {
        GpuBuffer count = resources.Get(indirect.CountResource);
        GpuBuffer arguments = resources.Get(indirect.ArgumentResource);
        Transition(count, ResourceStates.NonPixelShaderResource);
        Transition(arguments, ResourceStates.UnorderedAccess);
        commandList.SetPipelineState(indirectPipeline!);
        commandList.SetComputeRootSignature(rootSignature);
        commandList.SetComputeRootShaderResourceView(0, count.Resource.GPUVirtualAddress);
        commandList.SetComputeRootShaderResourceView(1, resources.GetOrDummy(null).Resource.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(2, arguments.Resource.GPUVirtualAddress);
        commandList.SetComputeRootUnorderedAccessView(3, resources.GetOrDummy(null).Resource.GPUVirtualAddress);
        uint words = checked((uint)arguments.ByteLength / 4);
        uint groups = (words + 63) / 64;
        uint groupsX = Math.Min(groups, 65_535u);
        uint[] constants = [checked((uint)indirect.CountByteOffset), checked((uint)indirect.ArgumentByteOffset),
            indirect.MaximumItemCount, indirect.ThreadsPerGroup, words, groupsX, 0, 0];
        commandList.SetComputeRoot32BitConstants(4, constants, 0);
        commandList.Dispatch(groupsX, (groups + groupsX - 1) / groupsX, 1);
        // UAV -> INDIRECT_ARGUMENT orders the GPU producer before ExecuteIndirect.
        Transition(arguments, ResourceStates.IndirectArgument);
    }

    private CorrectnessResult VerifyPlanOutputs(KernelExecutionPlan plan, PipelineSet pipelines,
        ResourceSet resources, Action<string, byte[]>? capture = null)
    {
        List<KernelOutputVerification> checks = [];
        byte[] poisons = [0xa5, 0x5a];
        for (int attempt = 0; attempt < poisons.Length; ++attempt)
        {
            // Poison every output before execution, including counts and arguments.
            foreach (KernelVerifiedOutput output in plan.GetVerifiedOutputs())
                PoisonVerifiedResource(resources.Get(output.Resource), poisons[attempt]);
            ExecutePlan(plan, pipelines, resources);
            ExecuteAndWait();
            foreach (KernelVerifiedOutput output in plan.GetVerifiedOutputs())
            {
                GpuBuffer buffer = resources.Get(output.Resource);
                Transition(buffer, ResourceStates.CopySource);
                using ID3D12Resource readback = device.CreateCommittedResource(HeapType.Readback,
                    ResourceDescription.Buffer((ulong)buffer.ByteLength, ResourceFlags.None, 0), ResourceStates.CopyDest, null);
                commandList.CopyResource(readback, buffer.Resource);
                ExecuteAndWait();
                Span<byte> bytes = readback.Map<byte>(0, buffer.ByteLength);
                string actual = ContentHash.Sha256(bytes);
                byte[]? captured = attempt == poisons.Length - 1 && capture is not null ? bytes.ToArray() : null;
                readback.Unmap(0);
                if (captured is not null) capture!(output.Resource, captured);
                checks.Add(new(output.Resource, attempt + 1, poisons[attempt], actual, output.ExpectedSha256,
                    string.Equals(actual, output.ExpectedSha256, StringComparison.OrdinalIgnoreCase)));
            }
        }
        bool passed = checks.All(check => check.Passed);
        string primary = checks.Last(check => check.Resource == plan.VerifiedResource).ActualSha256;
        return new CorrectnessResult(passed, primary, plan.ExpectedSha256,
            passed ? "All declared outputs matched independent CPU oracles after two poison/re-executions."
                : "One or more declared outputs failed a poison/re-execution CPU oracle check.") { Outputs = checks };
    }
}

public sealed record NativePlanValidation(string WorkloadId, string KernelAbiVersion, DeviceFingerprint Device,
    string KernelSha256, string ExecutorSha256, IReadOnlyDictionary<string, string> DxilSha256,
    long DeclaredBufferBytes, IReadOnlyList<double> DiagnosticSamplesMilliseconds, CorrectnessResult Correctness)
{
    public string GpuScope => KernelAbiV2.GpuScope;
    public string EvidenceKind => "native-correctness-smoke-not-formal-performance";
}
