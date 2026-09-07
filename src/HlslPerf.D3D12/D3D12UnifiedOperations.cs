using System.Diagnostics;
using System.Text.Json;
using HlslPerf.Core;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.Dxc;

namespace HlslPerf.D3D12;

public sealed partial class D3D12Tuner
{
    public UnifiedExecutor CreateUnifiedExecutor() => new(this);

    public bool IsDeviceRemoved => device.DeviceRemovedReason.Failure;
    public string DeviceRemovalStatus => device.DeviceRemovedReason.ToString();

    public static void EnableUnifiedDebugLayer()
    {
        using ID3D12Debug debug = Vortice.Direct3D12.D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        debug.EnableDebugLayer();
    }

    public string[] ReadUnifiedDebugMessages()
    {
        using ID3D12InfoQueue? info = device.QueryInterfaceOrNull<ID3D12InfoQueue>();
        if (info is null) return [];
        return Enumerable.Range(0, checked((int)info.NumStoredMessages)).Select(index =>
        {
            Message message = info.GetMessage((ulong)index);
            return $"{message.Severity}: {message.Id}: {message.Description}";
        }).ToArray();
    }

    private UnifiedSubmissionTiming UnifiedSubmitAndWait()
    {
        long start = Stopwatch.GetTimestamp();
        device.DeviceRemovedReason.CheckError();
        commandList.Close();
        double close = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        queue.ExecuteCommandList(commandList);
        ulong target = ++fenceValue;
        queue.Signal(fence, target).CheckError();
        double submit = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp();
        ulong completed = fence.CompletedValue;
        CheckCompletion(completed);
        if (completed < target)
        {
            fence.SetEventOnCompletion(target, fenceEvent).CheckError();
            if (!fenceEvent.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException($"Unified GPU fence timed out after 30 seconds; device reason: {DeviceRemovalStatus}.");
            completed = fence.CompletedValue;
            CheckCompletion(completed);
            if (completed < target) throw new InvalidDataException("GPU fence woke before the submitted work completed.");
        }
        double wait = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        device.DeviceRemovedReason.CheckError();
        start = Stopwatch.GetTimestamp();
        allocator.Reset();
        commandList.Reset(allocator);
        return new(close, submit, wait, Stopwatch.GetElapsedTime(start).TotalMilliseconds);

        void CheckCompletion(ulong value)
        {
            device.DeviceRemovedReason.CheckError();
            if (value == ulong.MaxValue)
                throw new InvalidOperationException($"GPU fence reported the device-removal sentinel; reason: {DeviceRemovalStatus}.");
        }
    }

    /// <summary>Owns one common root signature and PSO cache for every benchmark arm.</summary>
    public sealed class UnifiedExecutor : IDisposable
    {
        private readonly D3D12Tuner owner;
        private readonly Dictionary<string, ID3D12PipelineState> pipelines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> shaderIdentities = new(StringComparer.Ordinal);
        internal ID3D12RootSignature Root { get; }
        public List<object> CompilationEvidence { get; } = [];

        internal UnifiedExecutor(D3D12Tuner owner)
        {
            this.owner = owner;
            List<RootParameter1> parameters =
            [
                new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0, RootDescriptorFlags.None), ShaderVisibility.All),
                new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0, RootDescriptorFlags.None), ShaderVisibility.All),
                new(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0, RootDescriptorFlags.None), ShaderVisibility.All),
                new(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0, RootDescriptorFlags.None), ShaderVisibility.All),
                new(new RootConstants(0, 0, 8), ShaderVisibility.All)
            ];
            for (uint slot = 2; slot < 5; slot++)
                parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(slot, 0, RootDescriptorFlags.None), ShaderVisibility.All));
            Root = owner.device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters.ToArray(), []));
        }

        public UnifiedSession Prepare(UnifiedOperationPlan plan, long maximumAllocationBytes = 512L * 1024 * 1024)
        {
            ObjectDisposedException.ThrowIf(owner.disposed, owner);
            plan.Validate();
            foreach (UnifiedShader shader in plan.Shaders) PrepareShader(shader);
            return UnifiedSession.Create(owner, this, plan, maximumAllocationBytes);
        }

        private void PrepareShader(UnifiedShader shader)
        {
            string[] paths = new[] { shader.SourcePath }.Concat(shader.IncludeDirectories.SelectMany(directory =>
                Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(path =>
                    Path.GetExtension(path) is ".h" or ".hlsl" or ".hlsli")))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            var files = paths.Select(path => new { path, sha256 = ContentHash.Sha256(File.ReadAllBytes(path)) }).ToArray();
            string identity = ContentHash.Sha256(JsonSerializer.Serialize(new { shader, files, options = "O3-strict-SM66-strip-rootsignature-warnings-retained" }, JsonDefaults.Options));
            if (shaderIdentities.TryGetValue(shader.Id, out string? previous))
            {
                if (previous != identity) throw new InvalidDataException("Shader ID reused with changed source or options: " + shader.Id);
                return;
            }
            long start = Stopwatch.GetTimestamp();
            DxcCompilerOptions options = new() { ShaderModel = DxcShaderModel.Model6_6, OptimizationLevel = 3, EnableStrictness = true, WarningsAreErrors = false };
            string[] arguments = shader.IncludeDirectories.SelectMany(directory => new[] { "-I", directory })
                .Concat(shader.CompilerArguments).Append("-Qstrip_rootsignature").ToArray();
            using IDxcResult compiled = DxcCompiler.Compile(DxcShaderStage.Compute, File.ReadAllText(shader.SourcePath),
                shader.EntryPoint, options, shader.SourcePath,
                shader.Defines.Select(pair => new DxcDefine { Name = pair.Key, Value = pair.Value }).ToArray(), null, arguments);
            string diagnostics = compiled.GetErrors();
            if (compiled.GetStatus().Failure) throw new InvalidDataException($"{shader.Id}: {diagnostics}");
            byte[] dxil = compiled.GetObjectBytecodeArray();
            ID3D12PipelineState pipeline = owner.device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = Root, ComputeShader = dxil });
            pipelines.Add(shader.Id, pipeline);
            shaderIdentities.Add(shader.Id, identity);
            CompilationEvidence.Add(new { shader, files, identitySha256 = identity, dxilSha256 = ContentHash.Sha256(dxil), arguments,
                diagnostics, cpuCompileAndPsoMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
        }

        internal ID3D12PipelineState GetPipeline(string id) => pipelines[id];

        public void Dispose()
        {
            foreach (ID3D12PipelineState pipeline in pipelines.Values) pipeline.Dispose();
            Root.Dispose();
        }
    }

    public sealed class UnifiedSession : IDisposable
    {
        private const int QueryCount = 256;
        private readonly D3D12Tuner owner;
        private readonly UnifiedExecutor executor;
        private readonly UnifiedOperationPlan plan;
        private readonly ResourceSet resources;
        private readonly ID3D12QueryHeap queries;
        private readonly ID3D12Resource queryReadback;
        public ulong CommittedBytes { get; }
        public long LogicalBytes => plan.Buffers.Sum(buffer => (long)buffer.ByteLength);
        public double CpuPreparationMilliseconds { get; private set; }
        public double GpuUploadMilliseconds { get; private set; }
        public long UploadBytes => plan.Buffers.Where(buffer => buffer.InitialData is not null).Sum(buffer => (long)buffer.ByteLength);

        private UnifiedSession(D3D12Tuner owner, UnifiedExecutor executor, UnifiedOperationPlan plan, ResourceSet resources, ulong allocated)
        {
            this.owner = owner; this.executor = executor; this.plan = plan; this.resources = resources; CommittedBytes = allocated;
            queries = owner.device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, QueryCount, 0));
            queryReadback = owner.device.CreateCommittedResource(HeapType.Readback,
                ResourceDescription.Buffer(QueryCount * sizeof(ulong), ResourceFlags.None, 0), ResourceStates.CopyDest, null);
        }

        internal static UnifiedSession Create(D3D12Tuner owner, UnifiedExecutor executor, UnifiedOperationPlan plan, long cap)
        {
            long start = Stopwatch.GetTimestamp();
            ulong allocated = 0;
            foreach (int bytes in plan.Buffers.Select(buffer => buffer.ByteLength).Append(256))
                allocated = checked(allocated + owner.device.GetResourceAllocationInfo(0,
                    [ResourceDescription.Buffer((ulong)bytes, ResourceFlags.AllowUnorderedAccess, 0)]).SizeInBytes);
            if (allocated > (ulong)cap) throw new InvalidDataException($"Requested committed allocation {allocated} exceeds arm cap {cap}.");
            ScenarioMemorySnapshot memory = owner.CaptureScenarioMemory();
            if (memory.LocalBudgetBytes is ulong budget && memory.LocalUsageBytes is ulong usage &&
                (usage >= budget || allocated > (budget - usage) * 3 / 4))
                throw new InvalidDataException("Requested operation exceeds available local-memory budget.");
            ResourceSet resources = owner.CreateResources(plan.Buffers.Select(buffer => buffer with { InitialData = null }).ToArray());
            UnifiedSession session = new(owner, executor, plan, resources, allocated);
            try
            {
                session.Upload();
                session.CpuPreparationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                return session;
            }
            catch { session.Dispose(); throw; }
        }

        private void Stamp(uint index) => owner.commandList.EndQuery(queries, QueryType.Timestamp, index);

        private ulong[] Resolve(uint count, out UnifiedSubmissionTiming submission)
        {
            owner.commandList.ResolveQueryData(queries, QueryType.Timestamp, 0, count, queryReadback, 0);
            submission = owner.UnifiedSubmitAndWait();
            ulong[] data = queryReadback.Map<ulong>(0, checked((int)count)).ToArray();
            queryReadback.Unmap(0);
            return data;
        }

        private double Elapsed(ulong[] timestamps, int begin, int end)
        {
            if (timestamps[end] < timestamps[begin]) throw new InvalidDataException("Nonmonotonic GPU timestamps.");
            return (timestamps[end] - timestamps[begin]) * 1000.0 / owner.timestampFrequency;
        }

        private void Upload()
        {
            List<ID3D12Resource> uploads = [];
            try
            {
                Stamp(0);
                foreach (KernelBufferSpec buffer in plan.Buffers.Where(buffer => buffer.InitialData is not null))
                {
                    ID3D12Resource upload = owner.device.CreateCommittedResource(HeapType.Upload,
                        ResourceDescription.Buffer((ulong)buffer.ByteLength, ResourceFlags.None, 0), ResourceStates.GenericRead, null);
                    uploads.Add(upload);
                    buffer.InitialData!.AsSpan().CopyTo(upload.Map<byte>(0, buffer.ByteLength));
                    upload.Unmap(0);
                    GpuBuffer target = resources.Get(buffer.Name);
                    owner.Transition(target, ResourceStates.CopyDest);
                    owner.commandList.CopyResource(target.Resource, upload);
                }
                Stamp(1);
                GpuUploadMilliseconds = Elapsed(Resolve(2, out _), 0, 1);
            }
            finally { foreach (ID3D12Resource upload in uploads) upload.Dispose(); }
        }

        private void Execute(UnifiedPass pass)
        {
            if (pass.CopySource is { } source)
            {
                GpuBuffer from = resources.Get(source), to = resources.Get(pass.CopyDestination!);
                owner.Transition(from, ResourceStates.CopySource); owner.Transition(to, ResourceStates.CopyDest);
                owner.commandList.CopyBufferRegion(to.Resource, 0, from.Resource, 0, (ulong)pass.CopyBytes);
                return;
            }
            foreach (string input in pass.Srvs.OfType<string>().Distinct()) owner.Transition(resources.Get(input), ResourceStates.NonPixelShaderResource);
            foreach (string output in pass.Uavs.OfType<string>().Distinct()) owner.Transition(resources.Get(output), ResourceStates.UnorderedAccess);
            owner.commandList.SetComputeRootSignature(executor.Root);
            owner.commandList.SetPipelineState(executor.GetPipeline(pass.ShaderId!));
            for (uint i = 0; i < 2; i++)
                owner.commandList.SetComputeRootShaderResourceView(i, resources.GetOrDummy(i < pass.Srvs.Count ? pass.Srvs[(int)i] : null).Resource.GPUVirtualAddress);
            for (uint i = 0; i < 5; i++)
                owner.commandList.SetComputeRootUnorderedAccessView(i < 2 ? i + 2 : i + 3,
                    resources.GetOrDummy(i < pass.Uavs.Count ? pass.Uavs[(int)i] : null).Resource.GPUVirtualAddress);
            uint[] constants = new uint[8];
            for (int i = 0; i < pass.Constants.Count; i++) constants[i] = pass.Constants[i];
            owner.commandList.SetComputeRoot32BitConstants(4, constants, 0);
            owner.commandList.Dispatch(pass.Dispatch!.X, pass.Dispatch.Y, pass.Dispatch.Z);
            owner.commandList.ResourceBarrierUnorderedAccessView(null!);
        }

        public UnifiedBatchTiming MeasureBatch(int repetitions) => MeasureRingBatch([this], repetitions);

        /// <summary>Development-only dispatch isolation; never used for benchmark timings.</summary>
        public void DiagnosePasses(Action<string, double> completed)
        {
            foreach (UnifiedPass pass in plan.Passes)
            {
                Stamp(0); Execute(pass); Stamp(1);
                double elapsed = Elapsed(Resolve(2, out _), 0, 1);
                completed(pass.Name, elapsed);
            }
        }

        public static UnifiedBatchTiming MeasureRingBatch(IReadOnlyList<UnifiedSession> ring, int repetitions)
        {
            if (ring.Count == 0 || repetitions is < 1 or > 50 || repetitions % ring.Count != 0)
                throw new ArgumentOutOfRangeException(nameof(repetitions), "Every resident slot must execute equally often.");
            UnifiedSession first = ring[0];
            if (ring.Any(session => session.owner != first.owner || session.executor != first.executor ||
                session.plan.Implementation != first.plan.Implementation || session.plan.LogicalCount != first.plan.LogicalCount ||
                session.plan.SemanticId != first.plan.SemanticId)) throw new InvalidDataException("Incompatible resident ring.");
            long start = Stopwatch.GetTimestamp();
            first.Stamp(0);
            uint marker = 1;
            for (int run = 0; run < repetitions; run++)
            {
                UnifiedSession active = ring[run % ring.Count];
                first.Stamp(marker++);
                foreach (UnifiedStage stage in Enum.GetValues<UnifiedStage>())
                {
                    foreach (UnifiedPass pass in active.plan.Passes.Where(pass => pass.Stage == stage)) active.Execute(pass);
                    first.Stamp(marker++);
                }
            }
            first.Stamp(marker++);
            double record = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            ulong[] data = first.Resolve(marker, out UnifiedSubmissionTiming submission);
            double[] stages = new double[4];
            for (int run = 0; run < repetitions; run++)
                for (int stage = 0; stage < 4; stage++) stages[stage] += first.Elapsed(data, 1 + run * 5 + stage, 2 + run * 5 + stage);
            return new(repetitions, ring.Count, Enumerable.Repeat(repetitions / ring.Count, ring.Count).ToArray(),
                first.Elapsed(data, 0, (int)marker - 1), stages[0], stages[1], stages[2], stages[3], record, submission, (int)marker);
        }

        public IReadOnlyList<UnifiedVerification> Verify(Action<string, ReadOnlyMemory<byte>>? capture = null)
        {
            List<UnifiedVerification> verifications = [];
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                byte poison = attempt == 1 ? (byte)0xa5 : (byte)0x5a;
                foreach (KernelBufferSpec buffer in plan.Buffers.Where(buffer => !plan.ImmutableInputs.Contains(buffer.Name)))
                    owner.PoisonVerifiedResource(resources.Get(buffer.Name), poison);
                MeasureBatch(1);
                foreach (KernelVerifiedOutput output in plan.Outputs)
                {
                    long start = Stopwatch.GetTimestamp();
                    GpuBuffer buffer = resources.Get(output.Resource);
                    using ID3D12Resource readback = owner.device.CreateCommittedResource(HeapType.Readback,
                        ResourceDescription.Buffer((ulong)buffer.ByteLength, ResourceFlags.None, 0), ResourceStates.CopyDest, null);
                    Stamp(0); owner.Transition(buffer, ResourceStates.CopySource);
                    owner.commandList.CopyResource(readback, buffer.Resource); Stamp(1);
                    double gpuReadback = Elapsed(Resolve(2, out _), 0, 1);
                    byte[] actual = readback.Map<byte>(0, buffer.ByteLength).ToArray(); readback.Unmap(0);
                    string hash = ContentHash.Sha256(actual);
                    capture?.Invoke(output.Resource, actual);
                    verifications.Add(new(output.Resource, attempt, poison, output.ExpectedSha256, hash,
                        hash == output.ExpectedSha256, gpuReadback, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                }
            }
            return verifications;
        }

        public void Dispose()
        {
            queryReadback.Dispose(); queries.Dispose(); resources.Dispose();
        }
    }
}
