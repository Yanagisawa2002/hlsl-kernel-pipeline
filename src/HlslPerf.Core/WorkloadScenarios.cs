using System.Text.Json;
using System.Text.Json.Nodes;

namespace HlslPerf.Core;

/// <summary>Buffers are retained together, but cache residency is never guaranteed.</summary>
public sealed record WorkloadScenario(
    string Id,
    IReadOnlyList<int> InputSeeds,
    int? ElementCount = null,
    long MaximumAllocationBytes = 512L * 1024 * 1024)
{
    public const string Schema = "hlslperf.workload-scenario.v1";
    public string CachePolicy => InputSeeds.Count == 1
        ? "cache-warm-repeated-resident-buffers"
        : "rotating-resident-buffers-cache-state-uncontrolled";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || InputSeeds.Count is < 1 or > 32 ||
            InputSeeds.Any(seed => seed <= 0) || InputSeeds.Distinct().Count() != InputSeeds.Count ||
            ElementCount is <= 0 || MaximumAllocationBytes is <= 0 or > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Scenarios require an id, 1..32 distinct positive seeds, positive optional count and allocation cap <= 2 GiB.");
    }

    public TuningManifest Apply(TuningManifest manifest, int slot)
    {
        Validate();
        if ((uint)slot >= InputSeeds.Count) throw new ArgumentOutOfRangeException(nameof(slot));
        JsonObject node = JsonSerializer.SerializeToNode(manifest, JsonDefaults.Options)!.AsObject();
        node["correctness"]!["seed"] = InputSeeds[slot];
        if (manifest.Workload is not null)
        {
            node["workload"]!["parameters"]!["seed"] = InputSeeds[slot];
            if (ElementCount.HasValue)
            {
                if (!manifest.Workload.Parameters.ContainsKey("elementCount"))
                    throw new InvalidDataException("ElementCount override requires an elementCount workload parameter.");
                node["workload"]!["parameters"]!["elementCount"] = ElementCount.Value;
            }
        }
        if (ElementCount.HasValue) node["workItemCount"] = ElementCount.Value;
        TuningManifest result = node.Deserialize<TuningManifest>(JsonDefaults.Options)!;
        result.Validate();
        return result;
    }

    public IReadOnlyList<ScenarioPlanSlot> Build(TuningManifest manifest, IKernelWorkload workload, KernelCandidate candidate)
    {
        Validate();
        List<ScenarioPlanSlot> slots = [];
        long bytes = 0;
        for (int slot = 0; slot < InputSeeds.Count; slot++)
        {
            KernelExecutionPlan plan = workload.Build(Apply(manifest, slot), candidate);
            plan.Validate();
            if (plan.AbiVersion != manifest.KernelAbiVersion)
                throw new InvalidDataException("Scenario plan ABI does not match its manifest.");
            bytes = checked(bytes + plan.Buffers.Sum(buffer => (long)buffer.ByteLength));
            if (bytes > MaximumAllocationBytes)
                throw new InvalidDataException("Scenario logical buffers exceed the declared memory cap.");
            // Providers may reuse their CPU staging arrays on the next Build call.
            plan = plan with { Buffers = plan.Buffers.Select(buffer => buffer with
                { InitialData = buffer.InitialData?.ToArray() }).ToArray() };
            string inputHash = ContentHash.Sha256(string.Join("\n", plan.Buffers
                .Where(buffer => buffer.InitialData is not null)
                .OrderBy(buffer => buffer.Name, StringComparer.Ordinal)
                .Select(buffer => buffer.Name + ":" + buffer.ByteLength + ":" + ContentHash.Sha256(buffer.InitialData!))));
            slots.Add(new ScenarioPlanSlot(slot, InputSeeds[slot], inputHash, plan));
        }
        if (slots.Count > 1 && slots.Select(slot => slot.InputSha256).Distinct().Count() != slots.Count)
            throw new InvalidDataException("Workload did not produce distinct initialized buffers for changing seeds; scenario unsupported.");
        return slots;
    }
}

public sealed record ScenarioPlanSlot(int Slot, int InputSeed, string InputSha256, KernelExecutionPlan Plan);
public sealed record ScenarioSlotEvidence(int Slot, int InputSeed, string InputSha256, string ExpectedSha256, long LogicalBytes, int PassCount);
public sealed record ScenarioMemorySnapshot(DateTimeOffset CapturedUtc, ulong? LocalBudgetBytes, ulong? LocalUsageBytes, string Status);
public sealed record ScenarioVerification(int Slot, int InputSeed, string Resource, CorrectnessResult Correctness);
public sealed record ScenarioCompilerBinary(string Path, string Sha256, string? FileVersion);
public sealed record ScenarioSessionEvidence(
    string Schema, string ScenarioId, string CachePolicy, string IdentitySha256,
    DeviceFingerprint Device, string SourceSha256, string WorkloadImplementationSha256,
    IReadOnlyDictionary<string, string> DxilSha256, IReadOnlyList<ScenarioSlotEvidence> Slots,
    long LogicalBufferBytes, ulong CommittedAllocationBytes, long InitialUploadBytes,
    long MaximumVerificationReadbackBytes, string ResidencyPolicy,
    ScenarioMemorySnapshot MemoryBefore, ScenarioMemorySnapshot MemoryAfter,
    string TimingScope, string ThermalStatus, string InterferenceStatus)
{
    public IReadOnlyList<ScenarioCompilerBinary> NativeCompilerBinaries { get; init; } = [];
    public string NativeCompilerStatus { get; init; } = "unavailable";
}
