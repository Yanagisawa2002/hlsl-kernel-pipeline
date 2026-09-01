using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using HlslPerf.Core;
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

public sealed class D3D12Tuner : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _commandList;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12QueryHeap _timestampQueryHeap;
    private readonly ID3D12Resource _timestampReadback;
    private readonly AdapterDescription1 _adapterDescription;
    private readonly string _driverVersion;
    private readonly ulong _timestampFrequency;
    private ulong _fenceValue;
    private bool _disposed;

    public D3D12Tuner(string? adapterNameContains = null)
    {
        (_device, _adapterDescription, _driverVersion) = CreateDevice(adapterNameContains);

        _queue = _device.CreateCommandQueue(
            CommandListType.Compute,
            CommandQueuePriority.Normal,
            CommandQueueFlags.None,
            0);
        _allocator = _device.CreateCommandAllocator(CommandListType.Compute);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Compute,
            _allocator,
            null!);
        _fence = _device.CreateFence(0, FenceFlags.None);

        Result frequencyResult = _queue.GetTimestampFrequency(out ulong frequency);
        if (frequencyResult.Failure || frequency == 0)
            throw new InvalidOperationException($"The D3D12 compute queue did not expose a timestamp frequency ({frequencyResult}).");
        _timestampFrequency = frequency;

        RootParameter1[] parameters =
        [
            new(
                RootParameterType.UnorderedAccessView,
                new RootDescriptor1(0, 0, RootDescriptorFlags.DataVolatile),
                ShaderVisibility.All),
            new(new RootConstants(0, 0, 2), ShaderVisibility.All)
        ];
        RootSignatureDescription1 rootDescription = new(RootSignatureFlags.None, parameters, []);
        _rootSignature = _device.CreateRootSignature(rootDescription);

        QueryHeapDescription queryDescription = new(QueryHeapType.Timestamp, 2, 0);
        _timestampQueryHeap = _device.CreateQueryHeap<ID3D12QueryHeap>(queryDescription);
        _timestampReadback = _device.CreateCommittedResource(
            HeapType.Readback,
            ResourceDescription.Buffer(2 * sizeof(ulong), ResourceFlags.None, 0),
            ResourceStates.CopyDest,
            null);
    }

    public DeviceFingerprint DescribeDevice(string shaderModel = "6_0")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CreateFingerprint(shaderModel);
    }

    public TuningRunReport Run(
        TuningManifest manifest,
        string manifestPath,
        Action<TuningProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? compilerCacheDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        manifest.Validate();

        string fullManifestPath = Path.GetFullPath(manifestPath);
        string manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("The manifest path has no parent directory.");
        string kernelPath = Path.GetFullPath(Path.Combine(manifestDirectory, manifest.KernelPath));
        string shaderSource = File.ReadAllText(kernelPath);
        string manifestHash = ContentHash.Sha256(File.ReadAllBytes(fullManifestPath));
        string kernelHash = ContentHash.Sha256(File.ReadAllBytes(kernelPath));
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        KernelCandidate baseline = CandidateGenerator.ResolveBaseline(manifest, candidates);
        string? expectedHash = CorrectnessOracle.BuildExpectedSha256(manifest);
        List<CandidateResult> results = new(candidates.Count);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        for (int index = 0; index < candidates.Count; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KernelCandidate candidate = candidates[index];
            progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "compile", null));

            CompilationOutput compilation = Compile(
                shaderSource,
                kernelPath,
                kernelHash,
                manifest,
                candidate,
                compilerCacheDirectory);
            if (!compilation.Success)
            {
                CandidateResult compileFailure = new(
                    candidate.Id,
                    candidate.Defines,
                    false,
                    compilation.Diagnostics,
                    null,
                    [],
                    0,
                    null,
                    null,
                    false,
                    "DXC compilation failed.");
                results.Add(compileFailure);
                progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "failed", compileFailure));
                continue;
            }

            progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "measure", null));
            CandidateResult result = MeasureCandidate(
                manifest,
                candidate,
                compilation.Bytecode!,
                compilation.Diagnostics,
                expectedHash);

            if (expectedHash is null && result.Correctness is not null)
                expectedHash = result.Correctness.ActualSha256;

            results.Add(result);
            progress?.Invoke(new TuningProgress(index + 1, candidates.Count, candidate.Id, "complete", result));
        }

        SelectionResult? selection = CandidateSelector.Select(
            results,
            baseline.Id,
            manifest.MinimumRequiredSpeedup);
        return new TuningRunReport(
            "1.0",
            started,
            DateTimeOffset.UtcNow,
            fullManifestPath,
            manifestHash,
            kernelHash,
            CreateFingerprint(manifest.ShaderModel),
            baseline.Id,
            selection,
            results);
    }

    private CandidateResult MeasureCandidate(
        TuningManifest manifest,
        KernelCandidate candidate,
        ReadOnlyMemory<byte> bytecode,
        string? compilerDiagnostics,
        string? expectedHash)
    {
        int threadsPerGroup = candidate.GetRequired(manifest.ThreadsPerGroupParameter);
        int elementsPerThread = candidate.GetRequired(manifest.ElementsPerThreadParameter);
        if (threadsPerGroup is <= 0 or > 1024)
            throw new InvalidDataException($"Candidate '{candidate.Id}' has invalid group size {threadsPerGroup}; D3D12 allows 1..1024 threads.");

        long workItemsPerGroup = checked((long)threadsPerGroup * elementsPerThread);
        uint dispatchGroups = checked((uint)((manifest.WorkItemCount + workItemsPerGroup - 1) / workItemsPerGroup));
        if (dispatchGroups > 65_535)
            throw new InvalidDataException($"Candidate '{candidate.Id}' requires {dispatchGroups} X groups; v0.1 supports at most 65,535.");

        ComputePipelineStateDescription pipelineDescription = new()
        {
            RootSignature = _rootSignature,
            ComputeShader = bytecode
        };
        using ID3D12PipelineState pipelineState = _device.CreateComputePipelineState(pipelineDescription);
        int outputByteCount = checked(manifest.WorkItemCount * sizeof(uint));
        using ID3D12Resource output = _device.CreateCommittedResource(
            HeapType.Default,
            ResourceDescription.Buffer((ulong)outputByteCount, ResourceFlags.AllowUnorderedAccess, 0),
            ResourceStates.UnorderedAccess,
            null);
        using ID3D12Resource outputReadback = _device.CreateCommittedResource(
            HeapType.Readback,
            ResourceDescription.Buffer((ulong)outputByteCount, ResourceFlags.None, 0),
            ResourceStates.CopyDest,
            null);

        if (manifest.WarmupDispatches > 0)
        {
            int warmupDispatches = manifest.WarmupDispatches;
            double accumulatedWarmupMilliseconds = 0;
            do
            {
                accumulatedWarmupMilliseconds += MeasureBatch(
                    pipelineState,
                    output,
                    manifest,
                    dispatchGroups,
                    warmupDispatches);
                warmupDispatches = Math.Min(
                    manifest.MaximumDispatchesPerBatch,
                    checked(warmupDispatches * 2));
            }
            while (accumulatedWarmupMilliseconds < manifest.MinimumWarmupMilliseconds &&
                   warmupDispatches <= manifest.MaximumDispatchesPerBatch);
        }

        int measuredDispatchesPerBatch = manifest.DispatchesPerBatch;
        while (true)
        {
            double calibrationMilliseconds = MeasureBatch(
                pipelineState,
                output,
                manifest,
                dispatchGroups,
                measuredDispatchesPerBatch);
            if (calibrationMilliseconds >= manifest.MinimumBatchMilliseconds ||
                measuredDispatchesPerBatch >= manifest.MaximumDispatchesPerBatch)
                break;
            measuredDispatchesPerBatch = Math.Min(
                manifest.MaximumDispatchesPerBatch,
                checked(measuredDispatchesPerBatch * 2));
        }

        List<double> samples = new(manifest.MeasurementBatches);
        for (int batch = 0; batch < manifest.MeasurementBatches; ++batch)
        {
            double batchMilliseconds = MeasureBatch(
                pipelineState,
                output,
                manifest,
                dispatchGroups,
                measuredDispatchesPerBatch);
            samples.Add(batchMilliseconds / measuredDispatchesPerBatch);
        }

        _commandList.ResourceBarrierTransition(output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _commandList.CopyResource(outputReadback, output);
        ExecuteAndWait();

        Span<byte> outputBytes = outputReadback.Map<byte>(0, outputByteCount);
        string actualHash = ContentHash.Sha256(outputBytes);
        outputReadback.Unmap(0);
        string resolvedExpectedHash = expectedHash ?? actualHash;
        bool correctnessPassed = string.Equals(actualHash, resolvedExpectedHash, StringComparison.Ordinal);
        CorrectnessResult correctness = new(
            correctnessPassed,
            actualHash,
            resolvedExpectedHash,
            expectedHash is null
                ? "Established the cross-candidate reference hash."
                : correctnessPassed
                    ? "GPU output matched the correctness oracle."
                    : "GPU output did not match the correctness oracle.");

        DistributionSummary timing = StableStatistics.Summarize(samples);
        bool stable = timing.CoefficientOfVariation <= manifest.MaximumCoefficientOfVariation;
        double throughput = (manifest.WorkItemCount / (timing.MedianMilliseconds / 1000.0)) / 1_000_000.0;
        return new CandidateResult(
            candidate.Id,
            candidate.Defines,
            true,
            compilerDiagnostics,
            correctness,
            samples,
            measuredDispatchesPerBatch,
            timing,
            throughput,
            stable,
            correctnessPassed ? null : "Correctness gate failed.");
    }

    private void Bind(
        ID3D12PipelineState pipelineState,
        ID3D12Resource output,
        TuningManifest manifest)
    {
        _commandList.SetPipelineState(pipelineState);
        _commandList.SetComputeRootSignature(_rootSignature);
        _commandList.SetComputeRootUnorderedAccessView(0, output.GPUVirtualAddress);
        uint[] constants = [(uint)manifest.WorkItemCount, unchecked((uint)manifest.Correctness.Seed)];
        _commandList.SetComputeRoot32BitConstants(1, constants, 0);
    }

    private double MeasureBatch(
        ID3D12PipelineState pipelineState,
        ID3D12Resource output,
        TuningManifest manifest,
        uint dispatchGroups,
        int dispatchCount)
    {
        Bind(pipelineState, output, manifest);
        _commandList.EndQuery(_timestampQueryHeap, QueryType.Timestamp, 0);
        for (int dispatch = 0; dispatch < dispatchCount; ++dispatch)
            _commandList.Dispatch(dispatchGroups, 1, 1);
        _commandList.EndQuery(_timestampQueryHeap, QueryType.Timestamp, 1);
        _commandList.ResolveQueryData(_timestampQueryHeap, QueryType.Timestamp, 0, 2, _timestampReadback, 0);
        ExecuteAndWait();

        Span<ulong> timestamps = _timestampReadback.Map<ulong>(0, 2);
        ulong start = timestamps[0];
        ulong end = timestamps[1];
        _timestampReadback.Unmap(0);
        if (end <= start)
            throw new InvalidOperationException("GPU timestamps were not monotonic.");
        return (end - start) * 1000.0 / _timestampFrequency;
    }

    private void ExecuteAndWait()
    {
        _commandList.Close();
        _queue.ExecuteCommandList(_commandList);

        ulong target = ++_fenceValue;
        Result signalResult = _queue.Signal(_fence, target);
        if (signalResult.Failure)
            throw new InvalidOperationException($"Could not signal the D3D12 fence ({signalResult}).");
        if (_fence.CompletedValue < target)
        {
            Result eventResult = _fence.SetEventOnCompletion(target, _fenceEvent);
            if (eventResult.Failure)
                throw new InvalidOperationException($"Could not register the D3D12 fence event ({eventResult}).");
            _fenceEvent.WaitOne();
        }

        _allocator.Reset();
        _commandList.Reset(_allocator);
    }

    private static CompilationOutput Compile(
        string shaderSource,
        string kernelPath,
        string kernelHash,
        TuningManifest manifest,
        KernelCandidate candidate,
        string? compilerCacheDirectory)
    {
        string? cachePath = null;
        if (!string.IsNullOrWhiteSpace(compilerCacheDirectory))
        {
            string cacheIdentity = JsonSerializer.Serialize(new
            {
                schema = "dxc-cache-v1",
                kernelHash,
                manifest.EntryPoint,
                manifest.ShaderModel,
                compiler = typeof(DxcCompiler).Assembly.GetName().Version?.ToString(),
                defines = candidate.Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                options = "O3-strict-warnings-as-errors"
            }, JsonDefaults.Options);
            cachePath = Path.Combine(
                Path.GetFullPath(compilerCacheDirectory),
                ContentHash.Sha256(cacheIdentity) + ".dxil");
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
            manifest.EntryPoint,
            options,
            kernelPath,
            defines,
            null,
            []);
        string diagnostics = result.GetErrors();
        if (result.GetStatus().Failure)
            return new CompilationOutput(false, null, diagnostics);
        byte[] bytecode = result.GetObjectBytecodeArray();
        if (cachePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, bytecode);
        }
        return new CompilationOutput(
            true,
            bytecode,
            string.IsNullOrWhiteSpace(diagnostics) ? null : diagnostics.Trim());
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
            _adapterDescription.Description.TrimEnd('\0'),
            _adapterDescription.VendorId,
            _adapterDescription.DeviceId,
            _adapterDescription.SubsystemId,
            _adapterDescription.Revision,
            _adapterDescription.Luid.ToString(),
            _driverVersion,
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

                Result deviceResult = D3D12Api.D3D12CreateDevice(
                    adapter,
                    FeatureLevel.Level_11_0,
                    out ID3D12Device? device);
                if (deviceResult.Success && device is not null)
                    return (device, description, ReadDriverVersion(adapter, description));
            }
        }

        string suffix = string.IsNullOrWhiteSpace(adapterNameContains)
            ? string.Empty
            : $" matching '{adapterNameContains}'";
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
                if (adapterKey?.GetValue("DriverVersion") is string driverVersion &&
                    !string.IsNullOrWhiteSpace(driverVersion))
                    return driverVersion;
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
        if (_disposed)
            return;
        _disposed = true;
        _timestampReadback.Dispose();
        _timestampQueryHeap.Dispose();
        _rootSignature.Dispose();
        _commandList.Dispose();
        _allocator.Dispose();
        _queue.Dispose();
        _fence.Dispose();
        _device.Dispose();
        _fenceEvent.Dispose();
    }

    private sealed record CompilationOutput(bool Success, byte[]? Bytecode, string? Diagnostics);
}

internal static class CorrectnessOracle
{
    public static string? BuildExpectedSha256(TuningManifest manifest)
    {
        if (manifest.Correctness.Kind == "cross-candidate-sha256")
            return null;
        if (manifest.Correctness.Kind != "uint-mix-v1")
            throw new InvalidDataException($"Unsupported correctness oracle '{manifest.Correctness.Kind}'.");
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("The uint-mix-v1 oracle currently requires a little-endian CPU.");
        if (!manifest.FixedDefines.TryGetValue("HLSLPERF_ALU_ROUNDS", out int rounds))
            throw new InvalidDataException("uint-mix-v1 requires fixed define HLSLPERF_ALU_ROUNDS.");

        uint[] expected = new uint[manifest.WorkItemCount];
        uint seed = unchecked((uint)manifest.Correctness.Seed);
        Parallel.For(0, expected.Length, index =>
        {
            uint value = (uint)index ^ seed;
            for (uint round = 0; round < rounds; ++round)
            {
                value ^= value << 13;
                value ^= value >> 17;
                value ^= value << 5;
                value = unchecked(value * 1_664_525u + 1_013_904_223u + round);
            }
            expected[index] = value;
        });
        return ContentHash.Sha256(MemoryMarshal.AsBytes<uint>(expected.AsSpan()));
    }
}
