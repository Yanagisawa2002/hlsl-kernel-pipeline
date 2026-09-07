using System.Text.Json;
using System.Diagnostics;
using HlslPerf.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace HlslPerf.D3D12;

public sealed partial class D3D12Tuner
{
    /// <summary>Call serially on the owning tuner; keep paired arms alive together.</summary>
    public ScenarioSession PrepareScenario(TuningManifest manifest, string manifestPath,
        IKernelWorkload workload, KernelCandidate candidate, WorkloadScenario scenario,
        string? compilerCacheDirectory = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        manifest.Validate();
        IReadOnlyList<ScenarioPlanSlot> slots = scenario.Build(manifest, workload, candidate);
        string kernelPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.KernelPath));
        HlslSourceGraph graph = HlslSourceGraph.Load(kernelPath);
        return ScenarioSession.Create(this, manifest, workload, candidate, scenario, slots, graph, compilerCacheDirectory);
    }

    public ScenarioMemorySnapshot CaptureScenarioMemory()
    {
        try
        {
            using IDXGIFactory6 factory = Vortice.DXGI.DXGI.CreateDXGIFactory2<IDXGIFactory6>(false);
            for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
            {
                using (adapter)
                {
                    if (adapter!.Description1.Luid != adapterDescription.Luid) continue;
                    using IDXGIAdapter3 memoryAdapter = adapter.QueryInterface<IDXGIAdapter3>();
                    QueryVideoMemoryInfo info = memoryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    return new(DateTimeOffset.UtcNow, info.Budget, info.CurrentUsage, "DXGI process local-memory budget/usage; not per-resource residency proof");
                }
            }
            return new(DateTimeOffset.UtcNow, null, null, "unavailable: matching adapter not found");
        }
        catch (Exception exception)
        {
            return new(DateTimeOffset.UtcNow, null, null, "unavailable: " + exception.Message);
        }
    }

    public sealed class ScenarioSession : IDisposable
    {
        private readonly D3D12Tuner owner;
        private readonly IReadOnlyList<ScenarioPlanSlot> slots;
        private readonly PipelineSet pipelines;
        private readonly List<ResourceSet> resources;
        private bool disposed;
        public ScenarioSessionEvidence Evidence { get; }

        private ScenarioSession(D3D12Tuner owner, IReadOnlyList<ScenarioPlanSlot> slots,
            PipelineSet pipelines, List<ResourceSet> resources, ScenarioSessionEvidence evidence)
        {
            this.owner = owner;
            this.slots = slots;
            this.pipelines = pipelines;
            this.resources = resources;
            Evidence = evidence;
        }

        internal static ScenarioSession Create(D3D12Tuner owner, TuningManifest manifest,
            IKernelWorkload workload, KernelCandidate candidate, WorkloadScenario scenario,
            IReadOnlyList<ScenarioPlanSlot> slots, HlslSourceGraph graph, string? compilerCacheDirectory)
        {
            CompilationSet compilation = CompilePasses(graph.RootSource, graph.RootPath, graph.CombinedSha256,
                manifest, candidate, slots.SelectMany(slot => slot.Plan.Passes).Select(pass => pass.EntryPoint).Distinct(),
                compilerCacheDirectory, graph.IncludeDirectories);
            if (!compilation.Success) throw new InvalidDataException(compilation.Diagnostics);
            ulong allocated = 0;
            foreach (ScenarioPlanSlot slot in slots)
            {
                foreach (int bytes in slot.Plan.Buffers.Select(buffer => buffer.ByteLength).Append(256))
                {
                    ResourceDescription[] descriptions = [ResourceDescription.Buffer((ulong)bytes, ResourceFlags.AllowUnorderedAccess, 0)];
                    allocated = checked(allocated + owner.device.GetResourceAllocationInfo(0, descriptions).SizeInBytes);
                }
            }
            if (allocated > (ulong)scenario.MaximumAllocationBytes)
                throw new InvalidDataException("Committed default-heap allocation exceeds the scenario memory cap.");
            ScenarioMemorySnapshot before = owner.CaptureScenarioMemory();
            if (before.LocalBudgetBytes is ulong budget && before.LocalUsageBytes is ulong usage &&
                (usage >= budget || allocated > (budget - usage) * 3 / 4))
                throw new InvalidDataException("Scenario would consume more than 75% of the available DXGI local-memory budget.");
            PipelineSet pipelines = owner.CreatePipelines(compilation.Bytecodes);
            List<ResourceSet> resources = [];
            try
            {
                foreach (ScenarioPlanSlot slot in slots) resources.Add(owner.CreateResources(slot.Plan));
                ScenarioSlotEvidence[] slotEvidence = slots.Select(slot => new ScenarioSlotEvidence(slot.Slot,
                    slot.InputSeed, slot.InputSha256, slot.Plan.ExpectedSha256,
                    slot.Plan.Buffers.Sum(buffer => (long)buffer.ByteLength), slot.Plan.Passes.Count)).ToArray();
                Dictionary<string, string> dxilHashes = compilation.Bytecodes.ToDictionary(pair => pair.Key, pair => ContentHash.Sha256(pair.Value));
                ScenarioCompilerBinary[] compilerBinaries = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .Where(module => module.ModuleName.Equals("dxcompiler.dll", StringComparison.OrdinalIgnoreCase) ||
                        module.ModuleName.Equals("dxil.dll", StringComparison.OrdinalIgnoreCase))
                    .Select(module => new ScenarioCompilerBinary(module.FileName,
                        ContentHash.Sha256(File.ReadAllBytes(module.FileName)), module.FileVersionInfo.FileVersion)).ToArray();
                string workloadHash = WorkloadIdentity.Compute(workload);
                string identity = ContentHash.Sha256(JsonSerializer.Serialize(new
                {
                    schema = WorkloadScenario.Schema, scenario, manifest, candidate.Defines,
                    source = graph.CombinedSha256, workloadHash, slotEvidence, dxilHashes, compilerBinaries,
                    device = owner.CreateFingerprint(manifest.ShaderModel)
                }, JsonDefaults.Options));
                ScenarioSessionEvidence evidence = new(WorkloadScenario.Schema, scenario.Id, scenario.CachePolicy,
                    identity, owner.CreateFingerprint(manifest.ShaderModel), graph.CombinedSha256, workloadHash,
                    dxilHashes, slotEvidence, slotEvidence.Sum(slot => slot.LogicalBytes), allocated,
                    slots.Sum(slot => slot.Plan.Buffers.Where(buffer => buffer.InitialData is not null).Sum(buffer => (long)buffer.ByteLength)),
                    slots.Max(slot => (long)slot.Plan.Buffers.Single(buffer => buffer.Name == slot.Plan.VerifiedResource).ByteLength),
                    "Committed DEFAULT heaps retained for session; normal WDDM residency, no pinning or eviction trace; no guaranteed cache-cold state.",
                    before, owner.CaptureScenarioMemory(),
                    "GPU timestamps enclose every complete plan including resets and transitions; upload, PSO creation, poison, readback and host oracle excluded.",
                    "unavailable: temperature and clocks not sampled or controlled",
                    "uncontrolled: shared validation mutex serializes cooperating tasks only; external applications may interfere")
                {
                    NativeCompilerBinaries = compilerBinaries,
                    NativeCompilerStatus = compilerBinaries.Length > 0 ? "loaded native compiler module file hashes" :
                        "unavailable: no DXC module loaded; cached DXIL does not attest its original native compiler"
                };
                return new(owner, slots, pipelines, resources, evidence);
            }
            catch
            {
                foreach (ResourceSet resource in resources) resource.Dispose();
                pipelines.Dispose();
                throw;
            }
        }

        public double MeasureBatch(int runCount, int startSlot = 0)
        {
            ObjectDisposedException.ThrowIf(disposed || owner.disposed, this);
            if (runCount is < 1 or > 65536 || (uint)startSlot >= slots.Count)
                throw new ArgumentOutOfRangeException(nameof(runCount));
            owner.commandList.EndQuery(owner.timestampQueryHeap, QueryType.Timestamp, 0);
            for (int run = 0; run < runCount; run++)
            {
                int slot = (startSlot + run) % slots.Count;
                owner.ExecutePlan(slots[slot].Plan, pipelines, resources[slot]);
            }
            owner.commandList.EndQuery(owner.timestampQueryHeap, QueryType.Timestamp, 1);
            owner.commandList.ResolveQueryData(owner.timestampQueryHeap, QueryType.Timestamp, 0, 2, owner.timestampReadback, 0);
            owner.ExecuteAndWait();
            Span<ulong> timestamps = owner.timestampReadback.Map<ulong>(0, 2);
            ulong start = timestamps[0], end = timestamps[1];
            owner.timestampReadback.Unmap(0);
            if (end <= start) throw new InvalidOperationException("GPU timestamps were not monotonic.");
            return (end - start) * 1000.0 / owner.timestampFrequency;
        }

        public IReadOnlyList<ScenarioVerification> VerifyAll(Action<int, ReadOnlyMemory<byte>>? captureVerifiedOutput = null)
        {
            ObjectDisposedException.ThrowIf(disposed || owner.disposed, this);
            List<ScenarioVerification> results = [];
            foreach (ScenarioPlanSlot slot in slots)
            {
                CorrectnessResult correctness = owner.VerifyPlanOutputs(slot.Plan, pipelines, resources[slot.Slot],
                    captureVerifiedOutput is null ? null : (name, bytes) =>
                    {
                        if (name == slot.Plan.VerifiedResource) captureVerifiedOutput(slot.Slot, bytes);
                    });
                results.Add(new(slot.Slot, slot.InputSeed, slot.Plan.VerifiedResource, correctness));
            }
            return results;
        }

        public void Dispose()
        {
            if (disposed) return;
            foreach (ResourceSet resource in resources) resource.Dispose();
            pipelines.Dispose();
            disposed = true;
        }
    }
}

