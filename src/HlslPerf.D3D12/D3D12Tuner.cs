using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using Microsoft.Win32;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using D3D12Api = Vortice.Direct3D12.D3D12;
using DxgiApi = Vortice.DXGI.DXGI;

namespace HlslPerf.D3D12;

public sealed record TuningProgress(
    int CandidateIndex,
    int CandidateCount,
    string CandidateId,
    string Stage,
    CandidateResult? Result);

[SupportedOSPlatform("windows10.0")]
public sealed partial class D3D12Tuner : IDisposable
{
    private readonly ID3D12Device device;
    private readonly ID3D12CommandQueue queue;
    private readonly ID3D12CommandAllocator allocator;
    private readonly ID3D12GraphicsCommandList commandList;
    private readonly ID3D12Fence fence;
    private readonly AutoResetEvent fenceEvent = new(false);
    private readonly ID3D12RootSignature rootSignature;
    private readonly ID3D12QueryHeap timestampQueryHeap;
    private readonly ID3D12Resource timestampReadback;
    private readonly AdapterDescription1 adapterDescription;
    private readonly string driverVersion;
    private readonly ulong timestampFrequency;
    private ulong fenceValue;
    private bool disposed;

    public D3D12Tuner(string? adapterNameContains = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The HlslPerf D3D12 backend requires Windows.");
        (device, adapterDescription, driverVersion) = CreateDevice(adapterNameContains);
        queue = device.CreateCommandQueue(
            CommandListType.Compute,
            CommandQueuePriority.Normal,
            CommandQueueFlags.None,
            0);
        allocator = device.CreateCommandAllocator(CommandListType.Compute);
        commandList = device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Compute,
            allocator,
            null!);
        fence = device.CreateFence(0, FenceFlags.None);

        Result frequencyResult = queue.GetTimestampFrequency(out ulong frequency);
        if (frequencyResult.Failure || frequency == 0)
            throw new InvalidOperationException($"The D3D12 compute queue did not expose a timestamp frequency ({frequencyResult}).");
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

    public DeviceFingerprint DescribeDevice(string shaderModel = "6_0")
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return CreateFingerprint(shaderModel);
    }

    public TuningRunReport Run(
        TuningManifest manifest,
        string manifestPath,
        IKernelWorkload workload,
        Action<TuningProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? compilerCacheDirectory = null,
        Action<KernelCandidate, ReadOnlyMemory<byte>>? captureVerifiedOutput = null,
        TuningCheckpointOptions? checkpointOptions = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        manifest.Validate();
        ArgumentNullException.ThrowIfNull(workload);
        if (manifest.MeasurementProtocol == PairedProtocol.Id)
            return RunPaired(manifest, manifestPath, workload, progress, cancellationToken,
                compilerCacheDirectory, captureVerifiedOutput, checkpointOptions);

        string fullManifestPath = Path.GetFullPath(manifestPath);
        string manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("The manifest path has no parent directory.");
        string kernelPath = Path.GetFullPath(Path.Combine(manifestDirectory, manifest.KernelPath));
        HlslSourceGraph sourceGraph = HlslSourceGraph.Load(kernelPath);
        string shaderSource = sourceGraph.RootSource;
        string manifestHash = ContentHash.Sha256(File.ReadAllBytes(fullManifestPath));
        string kernelHash = sourceGraph.CombinedSha256;
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        KernelCandidate baseline = CandidateGenerator.ResolveBaseline(manifest, candidates);
        List<CandidateResult> results = new(candidates.Count);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        DeviceFingerprint fingerprint = CreateFingerprint(manifest.ShaderModel);
        string workloadImplementationHash = WorkloadIdentity.Compute(workload);
        Dictionary<string, CandidateResult> completed = new(StringComparer.Ordinal);
        DateTimeOffset checkpointCreated = started;
        if (checkpointOptions?.Resume == true)
        {
            TuningCheckpoint checkpoint = TuningCheckpointStore.LoadAndValidate(
                checkpointOptions.Path,
                manifestHash,
                kernelHash,
                fingerprint,
                workload.Id,
                workloadImplementationHash,
                KernelAbiV1.Id,
                candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal));
            checkpointCreated = checkpoint.CreatedUtc;
            foreach ((string id, CandidateResult result) in checkpoint.CompletedCandidates)
                if (CanResume(result))
                    completed[id] = result with { ReusedFromCheckpoint = true };
        }
        int reusedCandidateCount = 0;

        for (int index = 0; index < candidates.Count; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KernelCandidate candidate = candidates[index];
            if (completed.TryGetValue(candidate.Id, out CandidateResult? resumed))
            {
                results.Add(resumed);
                reusedCandidateCount++;
                progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "resumed", resumed));
                continue;
            }
            progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "compile", null));

            KernelExecutionPlan plan;
            try
            {
                plan = workload.Build(manifest, candidate);
                plan.Validate();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                CandidateResult planFailure = Failure(candidate, false, null, $"Execution plan failed: {exception.Message}");
                results.Add(planFailure);
                progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "failed", planFailure));
                WriteCheckpoint(complete: false);
                continue;
            }

            CompilationSet compilation = CompilePasses(
                shaderSource,
                kernelPath,
                kernelHash,
                manifest,
                candidate,
                plan.Passes.Select(pass => pass.EntryPoint).Distinct(StringComparer.Ordinal),
                compilerCacheDirectory,
                sourceGraph.IncludeDirectories);
            if (!compilation.Success)
            {
                CandidateResult compileFailure = Failure(candidate, false, compilation.Diagnostics, "DXC compilation failed.");
                results.Add(compileFailure);
                progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "failed", compileFailure));
                WriteCheckpoint(complete: false);
                continue;
            }

            progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "measure", null));
            CandidateResult result;
            try
            {
                result = MeasureCandidate(manifest, candidate, plan, compilation, captureVerifiedOutput);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = Failure(candidate, true, compilation.Diagnostics, $"GPU execution failed: {exception.Message}");
            }
            results.Add(result);
            progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "complete", result));
            WriteCheckpoint(complete: false);
        }

        SelectionResult? selection = CandidateSelector.Select(results, baseline.Id, manifest.MinimumRequiredSpeedup);
        WriteCheckpoint(complete: true);
        return new TuningRunReport(
            "2.0",
            started,
            DateTimeOffset.UtcNow,
            fullManifestPath,
            manifestHash,
            kernelHash,
            fingerprint,
            baseline.Id,
            selection,
            results,
            workload.Id,
            KernelAbiV1.Id,
            new TuningResumeSummary(
                checkpointOptions?.Resume == true,
                reusedCandidateCount,
                results.Count - reusedCandidateCount,
                TuningCheckpointStore.Schema));

        void WriteCheckpoint(bool complete)
        {
            if (checkpointOptions is null)
                return;
            IReadOnlyDictionary<string, CandidateResult> resumable = results
                .Where(CanResume)
                .ToDictionary(result => result.CandidateId, result => result, StringComparer.Ordinal);
            TuningCheckpointStore.Write(
                checkpointOptions.Path,
                new TuningCheckpoint(
                    TuningCheckpointStore.Schema,
                    HlslPerfSdk.Version,
                    HlslPerfSdk.MeasurementProtocol,
                    checkpointCreated,
                    DateTimeOffset.UtcNow,
                    complete,
                    manifestHash,
                    kernelHash,
                    fingerprint,
                    workload.Id,
                    workloadImplementationHash,
                    KernelAbiV1.Id,
                    resumable));
        }
    }

    private static bool CanResume(CandidateResult result) =>
        result.Compiled && result.Correctness is not null && result.Timing is not null;

    private static CandidateResult Failure(
        KernelCandidate candidate,
        bool compiled,
        string? diagnostics,
        string error) => new(
            candidate.Id,
            candidate.Defines,
            compiled,
            diagnostics,
            null,
            [],
            0,
            null,
            null,
            false,
            error);

    private CandidateResult MeasureCandidate(
        TuningManifest manifest,
        KernelCandidate candidate,
        KernelExecutionPlan plan,
        CompilationSet compilation,
        Action<KernelCandidate, ReadOnlyMemory<byte>>? captureVerifiedOutput,
        int? sampleCount = null)
    {
        using PipelineSet pipelines = CreatePipelines(compilation.Bytecodes);
        using ResourceSet resources = CreateResources(plan);

        if (manifest.WarmupDispatches > 0)
        {
            int warmupRuns = manifest.WarmupDispatches;
            double accumulatedWarmupMilliseconds = 0;
            do
            {
                accumulatedWarmupMilliseconds += MeasureBatch(plan, pipelines, resources, warmupRuns);
                warmupRuns = Math.Min(manifest.MaximumDispatchesPerBatch, checked(warmupRuns * 2));
            }
            while (accumulatedWarmupMilliseconds < manifest.MinimumWarmupMilliseconds &&
                   warmupRuns <= manifest.MaximumDispatchesPerBatch);
        }

        int measuredRunsPerBatch = manifest.DispatchesPerBatch;
        while (true)
        {
            double calibrationMilliseconds = MeasureBatch(plan, pipelines, resources, measuredRunsPerBatch);
            if (calibrationMilliseconds >= manifest.MinimumBatchMilliseconds ||
                measuredRunsPerBatch >= manifest.MaximumDispatchesPerBatch)
                break;
            measuredRunsPerBatch = Math.Min(manifest.MaximumDispatchesPerBatch, checked(measuredRunsPerBatch * 2));
        }

        List<double> samples = new(manifest.MeasurementBatches);
        for (int batch = 0; batch < (sampleCount ?? manifest.MeasurementBatches); ++batch)
        {
            double batchMilliseconds = MeasureBatch(plan, pipelines, resources, measuredRunsPerBatch);
            samples.Add(batchMilliseconds / measuredRunsPerBatch);
        }

        GpuBuffer verified = resources.Get(plan.VerifiedResource);
        PoisonVerifiedResource(verified);
        ExecutePlan(plan, pipelines, resources);
        ExecuteAndWait();
        Transition(verified, ResourceStates.CopySource);
        using ID3D12Resource readback = device.CreateCommittedResource(
            HeapType.Readback,
            ResourceDescription.Buffer((ulong)verified.ByteLength, ResourceFlags.None, 0),
            ResourceStates.CopyDest,
            null);
        commandList.CopyResource(readback, verified.Resource);
        ExecuteAndWait();

        Span<byte> outputBytes = readback.Map<byte>(0, verified.ByteLength);
        string actualHash = ContentHash.Sha256(outputBytes);
        byte[]? capturedOutput = captureVerifiedOutput is null ? null : outputBytes.ToArray();
        readback.Unmap(0);
        if (capturedOutput is not null)
            captureVerifiedOutput!(candidate, capturedOutput);
        bool correctnessPassed = string.Equals(actualHash, plan.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        CorrectnessResult correctness = new(
            correctnessPassed,
            actualHash,
            plan.ExpectedSha256,
            correctnessPassed
                ? "GPU output matched the workload pack's CPU oracle."
                : "GPU output did not match the workload pack's CPU oracle.");

        DistributionSummary timing = StableStatistics.Summarize(samples);
        bool stable = timing.CoefficientOfVariation <= manifest.MaximumCoefficientOfVariation;
        double throughput = (plan.LogicalItemCount / (timing.MedianMilliseconds / 1000.0)) / 1_000_000.0;
        return new CandidateResult(
            candidate.Id,
            candidate.Defines,
            true,
            compilation.Diagnostics,
            correctness,
            samples,
            measuredRunsPerBatch,
            timing,
            throughput,
            stable,
            correctnessPassed ? null : "Correctness gate failed.");
    }

    private void PoisonVerifiedResource(GpuBuffer verified)
    {
        using ID3D12Resource upload = device.CreateCommittedResource(
            HeapType.Upload,
            ResourceDescription.Buffer((ulong)verified.ByteLength, ResourceFlags.None, 0),
            ResourceStates.GenericRead,
            null);
        Span<byte> mapped = upload.Map<byte>(0, verified.ByteLength);
        mapped.Fill(0xa5);
        upload.Unmap(0);
        Transition(verified, ResourceStates.CopyDest);
        commandList.CopyResource(verified.Resource, upload);
        ExecuteAndWait();
    }

    private ResourceSet CreateResources(KernelExecutionPlan plan)
    {
        Dictionary<string, GpuBuffer> buffers = new(StringComparer.Ordinal);
        List<ID3D12Resource> uploads = [];
        try
        {
            foreach (KernelBufferSpec spec in plan.Buffers)
            {
                ResourceStates initialState = spec.InitialData is null
                    ? ResourceStates.NonPixelShaderResource
                    : ResourceStates.CopyDest;
                ID3D12Resource resource = device.CreateCommittedResource(
                    HeapType.Default,
                    ResourceDescription.Buffer((ulong)spec.ByteLength, ResourceFlags.AllowUnorderedAccess, 0),
                    initialState,
                    null);
                GpuBuffer buffer = new(spec.Name, spec.ByteLength, resource, initialState);
                buffers.Add(spec.Name, buffer);

                if (spec.InitialData is null)
                    continue;
                ID3D12Resource upload = device.CreateCommittedResource(
                    HeapType.Upload,
                    ResourceDescription.Buffer((ulong)spec.ByteLength, ResourceFlags.None, 0),
                    ResourceStates.GenericRead,
                    null);
                uploads.Add(upload);
                Span<byte> mapped = upload.Map<byte>(0, spec.ByteLength);
                spec.InitialData.AsSpan().CopyTo(mapped);
                upload.Unmap(0);
                commandList.CopyResource(resource, upload);
                Transition(buffer, ResourceStates.NonPixelShaderResource);
            }

            ID3D12Resource dummy = device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(256, ResourceFlags.AllowUnorderedAccess, 0),
                ResourceStates.Common,
                null);
            ResourceSet result = new(buffers, new GpuBuffer("$dummy", 256, dummy, ResourceStates.Common));
            if (uploads.Count > 0)
                ExecuteAndWait();
            return result;
        }
        catch
        {
            foreach (GpuBuffer buffer in buffers.Values)
                buffer.Dispose();
            throw;
        }
        finally
        {
            foreach (ID3D12Resource upload in uploads)
                upload.Dispose();
        }
    }

    private PipelineSet CreatePipelines(IReadOnlyDictionary<string, byte[]> bytecodes)
    {
        Dictionary<string, ID3D12PipelineState> pipelines = new(StringComparer.Ordinal);
        try
        {
            foreach ((string entryPoint, byte[] bytecode) in bytecodes)
            {
                ComputePipelineStateDescription description = new()
                {
                    RootSignature = rootSignature,
                    ComputeShader = bytecode
                };
                pipelines.Add(entryPoint, device.CreateComputePipelineState(description));
            }
            return new PipelineSet(pipelines);
        }
        catch
        {
            foreach (ID3D12PipelineState pipeline in pipelines.Values)
                pipeline.Dispose();
            throw;
        }
    }

    private double MeasureBatch(KernelExecutionPlan plan, PipelineSet pipelines, ResourceSet resources, int runCount)
    {
        commandList.EndQuery(timestampQueryHeap, QueryType.Timestamp, 0);
        for (int run = 0; run < runCount; ++run)
            ExecutePlan(plan, pipelines, resources);
        commandList.EndQuery(timestampQueryHeap, QueryType.Timestamp, 1);
        commandList.ResolveQueryData(timestampQueryHeap, QueryType.Timestamp, 0, 2, timestampReadback, 0);
        ExecuteAndWait();

        Span<ulong> timestamps = timestampReadback.Map<ulong>(0, 2);
        ulong start = timestamps[0];
        ulong end = timestamps[1];
        timestampReadback.Unmap(0);
        if (end <= start)
            throw new InvalidOperationException("GPU timestamps were not monotonic.");
        return (end - start) * 1000.0 / timestampFrequency;
    }

    private void ExecutePlan(KernelExecutionPlan plan, PipelineSet pipelines, ResourceSet resources)
    {
        foreach (KernelPassSpec pass in plan.Passes)
        {
            GpuBuffer input0 = resources.GetOrDummy(pass.Input0);
            GpuBuffer input1 = resources.GetOrDummy(pass.Input1);
            GpuBuffer output0 = resources.GetOrDummy(pass.Output0);
            GpuBuffer output1 = resources.GetOrDummy(pass.Output1);
            if (pass.Input0 is not null)
                Transition(input0, ResourceStates.NonPixelShaderResource);
            if (pass.Input1 is not null)
                Transition(input1, ResourceStates.NonPixelShaderResource);
            if (pass.Output0 is not null)
                Transition(output0, ResourceStates.UnorderedAccess);
            if (pass.Output1 is not null)
                Transition(output1, ResourceStates.UnorderedAccess);

            commandList.SetPipelineState(pipelines.Get(pass.EntryPoint));
            commandList.SetComputeRootSignature(rootSignature);
            commandList.SetComputeRootShaderResourceView(0, input0.Resource.GPUVirtualAddress);
            commandList.SetComputeRootShaderResourceView(1, input1.Resource.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(2, output0.Resource.GPUVirtualAddress);
            commandList.SetComputeRootUnorderedAccessView(3, output1.Resource.GPUVirtualAddress);
            uint[] constants = new uint[KernelAbiV1.RootConstantCount];
            for (int index = 0; index < pass.Constants.Count; ++index)
                constants[index] = pass.Constants[index];
            commandList.SetComputeRoot32BitConstants(4, constants, 0);
            commandList.Dispatch(pass.Dispatch.X, pass.Dispatch.Y, pass.Dispatch.Z);

            if (pass.Output0 is not null)
                Transition(output0, ResourceStates.NonPixelShaderResource);
            if (pass.Output1 is not null)
                Transition(output1, ResourceStates.NonPixelShaderResource);
        }
    }

    private void Transition(GpuBuffer buffer, ResourceStates target)
    {
        if (buffer.State == target)
            return;
        commandList.ResourceBarrierTransition(buffer.Resource, buffer.State, target);
        buffer.State = target;
    }

    private void ExecuteAndWait()
    {
        commandList.Close();
        queue.ExecuteCommandList(commandList);

        ulong target = ++fenceValue;
        Result signalResult = queue.Signal(fence, target);
        if (signalResult.Failure)
            throw new InvalidOperationException($"Could not signal the D3D12 fence ({signalResult}).");
        if (fence.CompletedValue < target)
        {
            Result eventResult = fence.SetEventOnCompletion(target, fenceEvent);
            if (eventResult.Failure)
                throw new InvalidOperationException($"Could not register the D3D12 fence event ({eventResult}).");
            fenceEvent.WaitOne();
        }

        allocator.Reset();
        commandList.Reset(allocator);
    }

    private static CompilationSet CompilePasses(
        string shaderSource,
        string kernelPath,
        string kernelHash,
        TuningManifest manifest,
        KernelCandidate candidate,
        IEnumerable<string> entryPoints,
        string? compilerCacheDirectory,
        IReadOnlyList<string> includeDirectories)
    {
        Dictionary<string, byte[]> bytecodes = new(StringComparer.Ordinal);
        List<string> diagnostics = [];
        foreach (string entryPoint in entryPoints)
        {
            CompilationOutput result = Compile(
                shaderSource,
                kernelPath,
                kernelHash,
                manifest,
                candidate,
                entryPoint,
                compilerCacheDirectory,
                includeDirectories);
            if (!string.IsNullOrWhiteSpace(result.Diagnostics))
                diagnostics.Add($"[{entryPoint}] {result.Diagnostics}");
            if (!result.Success)
                return new CompilationSet(false, bytecodes, string.Join(Environment.NewLine, diagnostics));
            bytecodes.Add(entryPoint, result.Bytecode!);
        }
        return new CompilationSet(true, bytecodes, diagnostics.Count == 0 ? null : string.Join(Environment.NewLine, diagnostics));
    }

    private static CompilationOutput Compile(
        string shaderSource,
        string kernelPath,
        string kernelHash,
        TuningManifest manifest,
        KernelCandidate candidate,
        string entryPoint,
        string? compilerCacheDirectory,
        IReadOnlyList<string> includeDirectories)
    {
        string? cachePath = null;
        if (!string.IsNullOrWhiteSpace(compilerCacheDirectory))
        {
            string cacheIdentity = JsonSerializer.Serialize(new
            {
                schema = "dxc-cache-v3",
                kernelHash,
                entryPoint,
                manifest.ShaderModel,
                abi = KernelAbiV1.Id,
                compiler = typeof(DxcCompiler).Assembly.GetName().Version?.ToString(),
                defines = candidate.Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                options = "O3-strict-warnings-as-errors"
            }, JsonDefaults.Options);
            cachePath = Path.Combine(Path.GetFullPath(compilerCacheDirectory), ContentHash.Sha256(cacheIdentity) + ".dxil");
            if (File.Exists(cachePath))
            {
                byte[] cachedBytecode = File.ReadAllBytes(cachePath);
                if (cachedBytecode.Length > 0)
                    return new CompilationOutput(true, cachedBytecode, "DXIL compiler cache hit.");
            }
        }

        DxcDefine[] defines = candidate.Defines
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DxcDefine
            {
                Name = pair.Key,
                Value = pair.Value.ToString(CultureInfo.InvariantCulture)
            })
            .ToArray();
        DxcCompilerOptions options = new()
        {
            ShaderModel = ParseShaderModel(manifest.ShaderModel),
            OptimizationLevel = 3,
            EnableStrictness = true,
            WarningsAreErrors = true
        };
        using IDxcResult result = DxcCompiler.Compile(
            DxcShaderStage.Compute,
            shaderSource,
            entryPoint,
            options,
            kernelPath,
            defines,
            null,
            includeDirectories.SelectMany(directory => new[] { "-I", directory }).ToArray());
        string compilerDiagnostics = result.GetErrors();
        if (result.GetStatus().Failure)
            return new CompilationOutput(false, null, compilerDiagnostics);
        byte[] bytecode = result.GetObjectBytecodeArray();
        if (cachePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, bytecode);
        }
        return new CompilationOutput(
            true,
            bytecode,
            string.IsNullOrWhiteSpace(compilerDiagnostics) ? null : compilerDiagnostics.Trim());
    }

    private static DxcShaderModel ParseShaderModel(string value) => value switch
    {
        "6_0" => DxcShaderModel.Model6_0,
        "6_1" => DxcShaderModel.Model6_1,
        "6_2" => DxcShaderModel.Model6_2,
        "6_3" => DxcShaderModel.Model6_3,
        "6_4" => DxcShaderModel.Model6_4,
        "6_5" => DxcShaderModel.Model6_5,
        "6_6" => DxcShaderModel.Model6_6,
        "6_7" => DxcShaderModel.Model6_7,
        _ => throw new InvalidDataException($"Unsupported DXC shader model '{value}'.")
    };

    private DeviceFingerprint CreateFingerprint(string shaderModel)
    {
        Version? bindingVersion = typeof(DxcCompiler).Assembly.GetName().Version;
        return new DeviceFingerprint(
            adapterDescription.Description.TrimEnd('\0'),
            adapterDescription.VendorId,
            adapterDescription.DeviceId,
            adapterDescription.SubsystemId,
            adapterDescription.Revision,
            adapterDescription.Luid.ToString(),
            driverVersion,
            "D3D12",
            shaderModel,
            $"Vortice.Dxc/{bindingVersion}",
            RuntimeInformation.OSDescription);
    }

    private static (ID3D12Device Device, AdapterDescription1 Description, string DriverVersion) CreateDevice(
        string? adapterNameContains)
    {
        using IDXGIFactory6 factory = DxgiApi.CreateDXGIFactory2<IDXGIFactory6>(false);
        for (uint adapterIndex = 0; ; ++adapterIndex)
        {
            Result enumResult = factory.EnumAdapterByGpuPreference(
                adapterIndex,
                GpuPreference.HighPerformance,
                out IDXGIAdapter1? adapter);
            if (enumResult.Failure || adapter is null)
                break;
            using (adapter)
            {
                AdapterDescription1 description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != AdapterFlags.None)
                    continue;
                if (!string.IsNullOrWhiteSpace(adapterNameContains) &&
                    !description.Description.Contains(adapterNameContains, StringComparison.OrdinalIgnoreCase))
                    continue;
                Result deviceResult = D3D12Api.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? createdDevice);
                if (deviceResult.Success && createdDevice is not null)
                    return (createdDevice, description, ReadDriverVersion(adapter, description));
            }
        }

        string suffix = string.IsNullOrWhiteSpace(adapterNameContains) ? string.Empty : $" matching '{adapterNameContains}'";
        throw new InvalidOperationException($"No hardware D3D12 adapter{suffix} was available.");
    }

    private static string ReadDriverVersion(IDXGIAdapter1 adapter, AdapterDescription1 description)
    {
        if (adapter.CheckInterfaceSupport<ID3D12Device>(out long rawVersion))
        {
            ulong value = unchecked((ulong)rawVersion);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{value >> 48}.{(value >> 32) & 0xffff}.{(value >> 16) & 0xffff}.{value & 0xffff}");
        }

        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        string vendorNeedle = $"VEN_{description.VendorId:X4}";
        string deviceNeedle = $"DEV_{description.DeviceId:X4}";
        string subsystemNeedle = $"SUBSYS_{description.SubsystemId:X8}";
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(displayClass);
            if (classKey is null)
                return "unavailable";
            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
                string? hardwareId = adapterKey?.GetValue("MatchingDeviceId") as string;
                if (hardwareId is null ||
                    !hardwareId.Contains(vendorNeedle, StringComparison.OrdinalIgnoreCase) ||
                    !hardwareId.Contains(deviceNeedle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (hardwareId.Contains("SUBSYS_", StringComparison.OrdinalIgnoreCase) &&
                    !hardwareId.Contains(subsystemNeedle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (adapterKey?.GetValue("DriverVersion") is string foundDriverVersion &&
                    !string.IsNullOrWhiteSpace(foundDriverVersion))
                    return foundDriverVersion;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return "unavailable";
        }
        return "unavailable";
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        timestampReadback.Dispose();
        timestampQueryHeap.Dispose();
        rootSignature.Dispose();
        commandList.Dispose();
        allocator.Dispose();
        queue.Dispose();
        fence.Dispose();
        device.Dispose();
        fenceEvent.Dispose();
    }

    private sealed record CompilationOutput(bool Success, byte[]? Bytecode, string? Diagnostics);
    private sealed record CompilationSet(bool Success, IReadOnlyDictionary<string, byte[]> Bytecodes, string? Diagnostics);

    private sealed class GpuBuffer : IDisposable
    {
        public GpuBuffer(string name, int byteLength, ID3D12Resource resource, ResourceStates state)
        {
            Name = name;
            ByteLength = byteLength;
            Resource = resource;
            State = state;
        }

        public string Name { get; }
        public int ByteLength { get; }
        public ID3D12Resource Resource { get; }
        public ResourceStates State { get; set; }
        public void Dispose() => Resource.Dispose();
    }

    private sealed class ResourceSet : IDisposable
    {
        private readonly IReadOnlyDictionary<string, GpuBuffer> buffers;
        private readonly GpuBuffer dummy;

        public ResourceSet(IReadOnlyDictionary<string, GpuBuffer> buffers, GpuBuffer dummy)
        {
            this.buffers = buffers;
            this.dummy = dummy;
        }

        public GpuBuffer Get(string name) => buffers.TryGetValue(name, out GpuBuffer? buffer)
            ? buffer
            : throw new InvalidDataException($"Unknown GPU buffer '{name}'.");

        public GpuBuffer GetOrDummy(string? name) => name is null ? dummy : Get(name);

        public void Dispose()
        {
            foreach (GpuBuffer buffer in buffers.Values)
                buffer.Dispose();
            dummy.Dispose();
        }
    }

    private sealed class PipelineSet : IDisposable
    {
        private readonly IReadOnlyDictionary<string, ID3D12PipelineState> pipelines;

        public PipelineSet(IReadOnlyDictionary<string, ID3D12PipelineState> pipelines) => this.pipelines = pipelines;

        public ID3D12PipelineState Get(string entryPoint) => pipelines.TryGetValue(entryPoint, out ID3D12PipelineState? pipeline)
            ? pipeline
            : throw new InvalidDataException($"No compiled pipeline exists for entry point '{entryPoint}'.");

        public void Dispose()
        {
            foreach (ID3D12PipelineState pipeline in pipelines.Values)
                pipeline.Dispose();
        }
    }
}
