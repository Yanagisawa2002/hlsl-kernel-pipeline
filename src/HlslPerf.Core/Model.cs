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
    public CorrectnessSpec Correctness { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != "1.0")
            throw new InvalidDataException($"Unsupported manifest schema '{SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(KernelPath))
            throw new InvalidDataException("Manifest name and kernelPath are required.");
        if (WorkItemCount <= 0 || WarmupDispatches < 0 || MeasurementBatches < 3 || DispatchesPerBatch <= 0)
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
        foreach (CandidateAxis axis in Axes)
            axis.Validate();
        foreach ((string key, int value) in BaselineDefines)
        {
            CandidateAxis axis = Axes.FirstOrDefault(candidateAxis => candidateAxis.Name == key)
                ?? throw new InvalidDataException($"Baseline key '{key}' is not a candidate axis.");
            if (!axis.Values.Contains(value))
                throw new InvalidDataException($"Baseline value {value} is not present in axis '{key}'.");
        }
        if (!Axes.Any(axis => axis.Name == ThreadsPerGroupParameter) && !FixedDefines.ContainsKey(ThreadsPerGroupParameter))
            throw new InvalidDataException($"Missing threads-per-group parameter '{ThreadsPerGroupParameter}'.");
        if (!Axes.Any(axis => axis.Name == ElementsPerThreadParameter) && !FixedDefines.ContainsKey(ElementsPerThreadParameter))
            throw new InvalidDataException($"Missing elements-per-thread parameter '{ElementsPerThreadParameter}'.");
    }

    public static TuningManifest Load(string path)
    {
        TuningManifest manifest = JsonSerializer.Deserialize<TuningManifest>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException($"Could not deserialize manifest '{path}'.");
        manifest.Validate();
        return manifest;
    }
}

public sealed class CandidateAxis
{
    public required string Name { get; init; }
    public required IReadOnlyList<int> Values { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Values.Count == 0)
            throw new InvalidDataException("Every candidate axis needs a name and at least one value.");
        if (Values.Any(value => value <= 0) || Values.Distinct().Count() != Values.Count)
            throw new InvalidDataException($"Axis '{Name}' values must be unique positive integers.");
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

public sealed record CorrectnessResult(bool Passed, string ActualSha256, string ExpectedSha256, string Detail);

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
    string? Error);

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
    IReadOnlyList<CandidateResult> Candidates);

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
    double? SpeedupOverBaseline);

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
