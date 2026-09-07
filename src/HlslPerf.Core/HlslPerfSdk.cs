namespace HlslPerf.Core;

public static class HlslPerfSdk
{
    public const string Version = "0.6.0";
    public const string ManifestSchema = "3.0";
    public const string ProfileSchema = "3.0";
    public const string CurrentCheckpointSchema = PairedProtocol.CheckpointSchema;
    public const string CurrentMeasurementProtocol = PairedProtocol.Id;
    // Retained source-compatible identities for the explicitly historical runner.
    public const string CheckpointSchema = "hlslperf.checkpoint.v1";
    public const string MeasurementProtocol = "gpu-timestamps-poison-reexecute-v1";
}
