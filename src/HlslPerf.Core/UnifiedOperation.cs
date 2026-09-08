namespace HlslPerf.Core;

/// <summary>Explicit operation adapter ABI: two SRVs, five UAVs, eight b0 constants.</summary>
public sealed record UnifiedShader(
    string Id, string SourcePath, string EntryPoint,
    IReadOnlyDictionary<string, string> Defines, IReadOnlyList<string> IncludeDirectories,
    IReadOnlyList<string> CompilerArguments)
{
    public int HlslVersion { get; init; } = 2018;
    public string ShaderModel { get; init; } = "6_6";
    public bool EnableStrictness { get; init; } = true;
}

public enum UnifiedStage { InputRestore, ScratchInitialization, Algorithm, OutputConversion }

public sealed record UnifiedPass(
    string Name, UnifiedStage Stage, string? ShaderId, KernelDispatch? Dispatch,
    IReadOnlyList<string?> Srvs, IReadOnlyList<string?> Uavs, IReadOnlyList<uint> Constants)
{
    public string? CopySource { get; init; }
    public string? CopyDestination { get; init; }
    public int CopyBytes { get; init; }
}

public sealed record UnifiedOperationPlan(
    string Implementation, int LogicalCount, string SemanticId, string InputSha256,
    IReadOnlyList<KernelBufferSpec> Buffers, IReadOnlyList<UnifiedShader> Shaders,
    IReadOnlyList<UnifiedPass> Passes, IReadOnlyList<KernelVerifiedOutput> Outputs)
{
    public const string AbiId = "hlslperf.unified-operation.v1";
    public IReadOnlyList<string> ImmutableInputs { get; init; } = [];

    public void Validate()
    {
        if (LogicalCount < 0 || string.IsNullOrWhiteSpace(Implementation) || InputSha256.Length != 64)
            throw new InvalidDataException("Unified operations require a bounded count and explicit input identity.");
        foreach (KernelBufferSpec buffer in Buffers) buffer.Validate();
        var buffers = Buffers.ToDictionary(b => b.Name, StringComparer.Ordinal);
        var shaders = Shaders.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (ImmutableInputs.Distinct().Count() != ImmutableInputs.Count ||
            ImmutableInputs.Any(name => !buffers.TryGetValue(name, out var buffer) || buffer.InitialData is null))
            throw new InvalidDataException("Immutable inputs must identify initialized operation buffers.");
        if (Passes.Count == 0 || Outputs.Count == 0) throw new InvalidDataException("Missing operation work or oracle.");
        UnifiedStage previous = UnifiedStage.InputRestore;
        foreach (UnifiedPass pass in Passes)
        {
            if (pass.Stage < previous) throw new InvalidDataException("Operation stages must be contiguous and ordered.");
            previous = pass.Stage;
            if (pass.CopySource is { } source)
            {
                if (!buffers.TryGetValue(source, out var input) ||
                    pass.CopyDestination is not { } destination || !buffers.TryGetValue(destination, out var output) ||
                    source == destination || pass.CopyBytes <= 0 || pass.CopyBytes > Math.Min(input.ByteLength, output.ByteLength) ||
                    pass.ShaderId is not null || pass.Dispatch is not null)
                    throw new InvalidDataException($"Invalid copy pass '{pass.Name}'.");
            }
            else
            {
                if (pass.ShaderId is null || !shaders.ContainsKey(pass.ShaderId) || pass.Dispatch is null ||
                    pass.Constants.Count > 8 || pass.Srvs.Count > 2 || pass.Uavs.Count > 5)
                    throw new InvalidDataException($"Invalid shader bindings in '{pass.Name}'.");
                pass.Dispatch.Validate(pass.Name);
                var srvs = pass.Srvs.OfType<string>().ToHashSet(StringComparer.Ordinal);
                var uavs = pass.Uavs.OfType<string>().ToHashSet(StringComparer.Ordinal);
                if (srvs.Overlaps(uavs) || srvs.Concat(uavs).Any(name => !buffers.ContainsKey(name)))
                    throw new InvalidDataException($"Aliased or unknown bindings in '{pass.Name}'.");
                // Upstream AMD scan deliberately binds one buffer through two UAV registers.
            }
        }
        if (Outputs.Select(o => o.Resource).Distinct().Count() != Outputs.Count ||
            Outputs.Any(o => !buffers.ContainsKey(o.Resource) || o.ExpectedSha256.Length != 64))
            throw new InvalidDataException("Invalid operation outputs.");
    }
}

public sealed record UnifiedSubmissionTiming(
    double CpuCloseMilliseconds, double CpuSubmitMilliseconds,
    double CpuFenceWaitMilliseconds, double CpuResetMilliseconds);

public sealed record UnifiedBatchTiming(
    int Repetitions, int ResidentSlots, IReadOnlyList<int> SlotExecutions,
    double GpuTotalMilliseconds, double GpuInputRestoreMilliseconds,
    double GpuScratchInitializationMilliseconds, double GpuAlgorithmMilliseconds,
    double GpuOutputConversionMilliseconds, double CpuRecordMilliseconds,
    UnifiedSubmissionTiming Submission, int TimestampMarkers)
{
    public IReadOnlyList<double> GpuOperationMilliseconds { get; init; } = [];
}

public sealed record UnifiedVerification(
    string Resource, int Attempt, byte PoisonByte, string ExpectedSha256,
    string ActualSha256, bool Passed, double GpuReadbackMilliseconds,
    double CpuReadbackMilliseconds);

public sealed record UnifiedPassTiming(string Name, UnifiedStage Stage, double GpuMilliseconds);
public sealed record UnifiedPassDiagnostic(double GpuTotalMilliseconds, double CpuRecordMilliseconds,
    UnifiedSubmissionTiming Submission, IReadOnlyList<UnifiedPassTiming> Passes, int TimestampMarkers)
{
    public int Repetitions { get; init; } = 1;
    public IReadOnlyList<double> GpuOperationMilliseconds { get; init; } = [];
}
