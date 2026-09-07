namespace HlslPerf.Core;

public sealed record EvidenceFile(string Path, string Sha256, long Bytes, string? Version = null);
public sealed record RgaInvocationEvidence(
    string Schema, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    EvidenceFile Executable, IReadOnlyList<EvidenceFile> CompilerFiles,
    IReadOnlyList<string> Arguments, string EntryPoint, string ShaderModel,
    IReadOnlyDictionary<string, int> Defines, string SourceGraphSha256,
    IReadOnlyList<HlslSourceDependency> Sources, DeviceFingerprint? MeasuredDevice,
    int ExitCode, IReadOnlyList<EvidenceFile> Artifacts, string CompilationRelationship);
