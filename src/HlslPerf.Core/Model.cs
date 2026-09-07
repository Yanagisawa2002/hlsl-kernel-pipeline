using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlslPerf.Core;

public sealed class TuningManifest
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string Name { get; init; }
    public required string KernelPath { get; init; }
    public string KernelAbiVersion { get; init; } = KernelAbiV1.Id;
    public WorkloadSpec? Workload { get; init; }
    public string EntryPoint { get; init; } = "CSMain";
    public string ShaderModel { get; init; } = "6_0";
    public int WorkItemCount { get; init; } = 1_048_576;
    public int WarmupDispatches { get; init; } = 8;
    public double MinimumWarmupMilliseconds { get; init; } = 75;
    public int MeasurementBatches { get; init; } = 15;
    public int DispatchesPerBatch { get; init; } = 8;
    public double MinimumBatchMilliseconds { get; init; } = 8;
    public int MaximumDispatchesPerBatch { get; init; } = 256;
    public double MaximumCoefficientOfVariation { get; init; } = 0.05;
    public double MinimumRequiredSpeedup { get; init; } = 1.01;
    public string ThreadsPerGroupParameter { get; init; } = "HLSLPERF_GROUP_SIZE";
    public string ElementsPerThreadParameter { get; init; } = "HLSLPERF_ELEMENTS_PER_THREAD";
    public IReadOnlyDictionary<string, int> FixedDefines { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    public IReadOnlyDictionary<string, int> BaselineDefines { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    public required IReadOnlyList<CandidateAxis> Axes { get; init; }
    public IReadOnlyList<CandidateConstraint> Constraints { get; init; } = [];
    public CorrectnessSpec Correctness { get; init; } = new();
    public string MeasurementProtocol { get; init; } = PairedProtocol.Id;
    public PairedMeasurementOptions PairedMeasurement { get; init; } = new();

    public void Validate()
    {
        if (MeasurementProtocol != PairedProtocol.Id && MeasurementProtocol != HlslPerfSdk.MeasurementProtocol)
            throw new InvalidDataException("Unknown measurement protocol.");
        if (MeasurementProtocol == PairedProtocol.Id)
            PairedMeasurement.Validate();
        if (SchemaVersion is not ("1.0" or "2.0" or "3.0"))
            throw new InvalidDataException($"Unsupported manifest schema '{SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(KernelPath))
            throw new InvalidDataException("Manifest name and kernelPath are required.");
        if (WorkItemCount < 0 || (WorkItemCount == 0 && (KernelAbiVersion != KernelAbiV2.Id || SchemaVersion == "1.0")) ||
            WarmupDispatches < 0 || MeasurementBatches < 3 || DispatchesPerBatch <= 0)
            throw new InvalidDataException("Workload counts must be positive and at least three measurement batches are required.");
        if (MinimumWarmupMilliseconds < 0 || MinimumBatchMilliseconds <= 0 ||
            MaximumDispatchesPerBatch < DispatchesPerBatch)
            throw new InvalidDataException("Calibration durations must be valid and maximumDispatchesPerBatch must cover dispatchesPerBatch.");
        if (MaximumCoefficientOfVariation <= 0 || MaximumCoefficientOfVariation > 1)
            throw new InvalidDataException("maximumCoefficientOfVariation must be in (0, 1].");
        if (MinimumRequiredSpeedup < 1)
            throw new InvalidDataException("minimumRequiredSpeedup must be at least 1.");
        if (Axes.Count == 0)
            throw new InvalidDataException("At least one candidate axis is required.");
        if (Axes.Select(axis => axis.Name).Distinct(StringComparer.Ordinal).Count() != Axes.Count)
            throw new InvalidDataException("Candidate axis names must be unique.");
        if (Axes.Any(axis => FixedDefines.ContainsKey(axis.Name)))
            throw new InvalidDataException("A define cannot be both fixed and a candidate axis.");
        HashSet<string> availableConditionNames = FixedDefines.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (CandidateAxis axis in Axes)
        {
            axis.Validate();
            ValidateCondition(axis.When, availableConditionNames, $"axis '{axis.Name}' when");
            availableConditionNames.Add(axis.Name);
        }
        HashSet<string> allDefineNames = availableConditionNames;
        foreach (CandidateConstraint constraint in Constraints)
            constraint.Validate(allDefineNames);
        if (SchemaVersion != "3.0" &&
            (Constraints.Count > 0 || Axes.Any(axis => axis.When.Count > 0)))
            throw new InvalidDataException("Conditional axes and constraints require manifest schema 3.0.");
        foreach ((string key, int value) in BaselineDefines)
        {
            CandidateAxis axis = Axes.FirstOrDefault(candidateAxis => candidateAxis.Name == key)
                ?? throw new InvalidDataException($"Baseline key '{key}' is not a candidate axis.");
            if (!axis.Values.Contains(value))
                throw new InvalidDataException($"Baseline value {value} is not present in axis '{key}'.");
        }
        if (SchemaVersion == "1.0")
        {
            if (!Axes.Any(axis => axis.Name == ThreadsPerGroupParameter) && !FixedDefines.ContainsKey(ThreadsPerGroupParameter))
                throw new InvalidDataException($"Missing threads-per-group parameter '{ThreadsPerGroupParameter}'.");
            if (!Axes.Any(axis => axis.Name == ElementsPerThreadParameter) && !FixedDefines.ContainsKey(ElementsPerThreadParameter))
                throw new InvalidDataException($"Missing elements-per-thread parameter '{ElementsPerThreadParameter}'.");
        }
        else
        {
            if (KernelAbiVersion != KernelAbiV1.Id && KernelAbiVersion != KernelAbiV2.Id)
                throw new InvalidDataException($"Unsupported kernel ABI '{KernelAbiVersion}'.");
            Workload?.Validate(KernelAbiVersion == KernelAbiV2.Id);
            if (Workload is null)
                throw new InvalidDataException("Schema 2.0+ manifests require a workload object.");
        }
    }

    internal static void ValidateCondition(
        IReadOnlyDictionary<string, IReadOnlyList<int>> condition,
        IReadOnlySet<string> availableNames,
        string owner)
    {
        foreach ((string name, IReadOnlyList<int> values) in condition)
        {
            if (string.IsNullOrWhiteSpace(name) || !availableNames.Contains(name))
                throw new InvalidDataException($"{owner} references unavailable define '{name}'.");
            if (values.Count == 0 || values.Any(value => value <= 0) || values.Distinct().Count() != values.Count)
                throw new InvalidDataException($"{owner} values for '{name}' must be unique positive integers.");
        }
    }

    public static TuningManifest Load(string path)
    {
        TuningManifest manifest = JsonSerializer.Deserialize<TuningManifest>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException($"Could not deserialize manifest '{path}'.");
        manifest.Validate();
        return manifest;
    }
}

public sealed class WorkloadSpec
{
    public required string Id { get; init; }
    public IReadOnlyDictionary<string, long> Parameters { get; init; } =
        new ReadOnlyDictionary<string, long>(new Dictionary<string, long>());

    internal void Validate(bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidDataException("workload.id is required.");
        if (Parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0 || (!allowZero && pair.Value == 0)))
            throw new InvalidDataException(allowZero
                ? "Workload parameter names must be non-empty and v2 values must be non-negative integers."
                : "Workload parameter names must be non-empty and values must be positive integers.");
    }

    public int GetRequiredInt32(string name)
    {
        if (!Parameters.TryGetValue(name, out long value))
            throw new InvalidDataException($"Workload '{Id}' is missing parameter '{name}'.");
        return checked((int)value);
    }

    public int GetInt32(string name, int fallback) => Parameters.TryGetValue(name, out long value)
        ? checked((int)value)
        : fallback;
}

public sealed class CandidateAxis
{
    public required string Name { get; init; }
    public required IReadOnlyList<int> Values { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<int>> When { get; init; } =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Values.Count == 0)
            throw new InvalidDataException("Every candidate axis needs a name and at least one value.");
        if (Values.Any(value => value <= 0) || Values.Distinct().Count() != Values.Count)
            throw new InvalidDataException($"Axis '{Name}' values must be unique positive integers.");
    }
}

public sealed class CandidateConstraint
{
    [JsonPropertyName("if")]
    public IReadOnlyDictionary<string, IReadOnlyList<int>> If { get; init; } =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

    [JsonPropertyName("then")]
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Then { get; init; } =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

    internal void Validate(IReadOnlySet<string> availableNames)
    {
        TuningManifest.ValidateCondition(If, availableNames, "constraint if");
        TuningManifest.ValidateCondition(Then, availableNames, "constraint then");
        if (Then.Count == 0)
            throw new InvalidDataException("A candidate constraint requires a non-empty then condition.");
    }
}

public sealed class CorrectnessSpec
{
    public string Kind { get; init; } = "cross-candidate-sha256";
    public int Seed { get; init; } = 0x1234567;
}

public sealed record KernelCandidate(IReadOnlyDictionary<string, int> Defines)
{
    [JsonIgnore]
    public string Id => string.Join("__", Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => $"{Sanitize(pair.Key)}-{pair.Value.ToString(CultureInfo.InvariantCulture)}"));

    public int GetRequired(string name) => Defines.TryGetValue(name, out int value)
        ? value
        : throw new InvalidDataException($"Candidate '{Id}' is missing required define '{name}'.");

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
}

public sealed record DistributionSummary(
    double MinimumMilliseconds,
    double MedianMilliseconds,
    double MeanMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    double StandardDeviationMilliseconds,
    double CoefficientOfVariation);

public sealed record CorrectnessResult(bool Passed, string ActualSha256, string ExpectedSha256, string Detail)
{
    public IReadOnlyList<KernelOutputVerification> Outputs { get; init; } = [];
}

public sealed record CandidateResult(
    string CandidateId,
    IReadOnlyDictionary<string, int> Defines,
    bool Compiled,
    string? CompilerDiagnostics,
    CorrectnessResult? Correctness,
    IReadOnlyList<double> SamplesMilliseconds,
    int MeasuredDispatchesPerBatch,
    DistributionSummary? Timing,
    double? ThroughputMillionItemsPerSecond,
    bool Stable,
    string? Error,
    RgaCandidateAnalysis? StaticAnalysis = null,
    bool ReusedFromCheckpoint = false);

public sealed record RgaPassAnalysis(
    string PassName,
    string EntryPoint,
    int? VgprsUsed,
    int? VgprsAvailable,
    int? PhysicalVgprs,
    int? MaximumLiveVgprs,
    int? AllocatedVgprs,
    double? VgprUsageRatio,
    int? SgprsUsed,
    int? SgprsAvailable,
    int? PhysicalSgprs,
    double? SgprUsageRatio,
    int? LdsBytes,
    int? LdsAvailableBytes,
    int? ScratchBytes,
    int? ThreadGroupX,
    int? ThreadGroupY,
    int? ThreadGroupZ,
    double? OccupancyWavesPerSimd,
    string? IsaPath,
    string? LiveVgprPath);

public sealed record RgaCandidateAnalysis(
    string Tool,
    string? ToolVersion,
    string? Target,
    string Status,
    IReadOnlyList<RgaPassAnalysis> Passes,
    string? Diagnostic);

public sealed record DeviceFingerprint(
    string AdapterName,
    uint VendorId,
    uint DeviceId,
    uint SubsystemId,
    uint Revision,
    string AdapterLuid,
    string DriverVersion,
    string Backend,
    string ShaderModel,
    string CompilerVersion,
    string OperatingSystem);

public sealed record SelectionResult(
    string CandidateId,
    string ObservedFastestCandidateId,
    bool UsedStablePool,
    bool RetainedBaseline,
    string Reason);

public sealed record TuningRunReport(
    string SchemaVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string ManifestPath,
    string ManifestSha256,
    string KernelSha256,
    DeviceFingerprint Device,
    string BaselineCandidateId,
    SelectionResult? Selection,
    IReadOnlyList<CandidateResult> Candidates,
    string WorkloadId = "legacy-v1",
    string KernelAbiVersion = "legacy-v1",
    TuningResumeSummary? Resume = null,
    string MeasurementProtocol = HlslPerfSdk.MeasurementProtocol,
    PairedRunEvidence? PairedEvidence = null);

public sealed record TuningResumeSummary(
    bool Enabled,
    int ReusedCandidateCount,
    int MeasuredCandidateCount,
    string CheckpointSchema);

public sealed record ProfileDefine(string Name, int Value);

public sealed record TuningProfile(
    string SchemaVersion,
    DateTimeOffset CreatedUtc,
    string CompatibilityKey,
    DeviceFingerprint Device,
    string ManifestSha256,
    string KernelSha256,
    string CandidateId,
    IReadOnlyDictionary<string, int> Defines,
    double MedianGpuMilliseconds,
    double P95GpuMilliseconds,
    double ThroughputMillionItemsPerSecond,
    double? SpeedupOverBaseline,
    string WorkloadId = "legacy-v1",
    string KernelAbiVersion = "legacy-v1",
    IReadOnlyList<ProfileDefine>? DefineValues = null,
    string MeasurementProtocol = HlslPerfSdk.MeasurementProtocol,
    string EvidenceStatus = "historical",
    string? WorkloadImplementationSha256 = null,
    string? ExecutionIdentitySha256 = null,
    string? ConfirmationSha256 = null);

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.Strict
    };
}

public static class ContentHash
{
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    public static string CompatibilityKey(DeviceFingerprint device, string manifestSha256, string kernelSha256)
    {
        string canonical = JsonSerializer.Serialize(new
        {
            device.AdapterName,
            device.VendorId,
            device.DeviceId,
            device.SubsystemId,
            device.Revision,
            device.AdapterLuid,
            device.DriverVersion,
            device.Backend,
            device.ShaderModel,
            device.CompilerVersion,
            manifestSha256,
            kernelSha256
        }, JsonDefaults.Options);
        return Sha256(canonical);
    }
}
