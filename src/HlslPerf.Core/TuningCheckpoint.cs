using System.Text;
using System.Text.Json;

namespace HlslPerf.Core;

public sealed record TuningCheckpointOptions(string Path, bool Resume);

public sealed record TuningCheckpoint(
    string SchemaVersion,
    string SdkVersion,
    string MeasurementProtocol,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    bool Complete,
    string ManifestSha256,
    string KernelSha256,
    DeviceFingerprint Device,
    string WorkloadId,
    string WorkloadImplementationSha256,
    string KernelAbiVersion,
    IReadOnlyDictionary<string, CandidateResult> CompletedCandidates);

public static class TuningCheckpointStore
{
    public const string Schema = HlslPerfSdk.CheckpointSchema;

    public static TuningCheckpoint LoadAndValidate(
        string path,
        string manifestSha256,
        string kernelSha256,
        DeviceFingerprint device,
        string workloadId,
        string workloadImplementationSha256,
        string kernelAbiVersion,
        IReadOnlySet<string> candidateIds)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Tuning checkpoint was not found.", fullPath);
        TuningCheckpoint checkpoint = JsonSerializer.Deserialize<TuningCheckpoint>(
            File.ReadAllText(fullPath),
            JsonDefaults.Options) ?? throw new InvalidDataException("Tuning checkpoint JSON is empty.");
        if (checkpoint.SchemaVersion != Schema)
            throw new InvalidDataException($"Unsupported tuning checkpoint schema '{checkpoint.SchemaVersion}'.");
        if (checkpoint.SdkVersion != HlslPerfSdk.Version ||
            checkpoint.MeasurementProtocol != HlslPerfSdk.MeasurementProtocol)
            throw new InvalidDataException(
                "Checkpoint SDK or measurement protocol does not match this runner.");
        if (!string.Equals(checkpoint.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(checkpoint.KernelSha256, kernelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checkpoint manifest or transitive kernel hash does not match this run.");
        if (checkpoint.Device != device)
            throw new InvalidDataException(
                "Checkpoint device, driver, backend, shader model, or compiler does not match this run.");
        if (checkpoint.WorkloadId != workloadId || checkpoint.KernelAbiVersion != kernelAbiVersion)
            throw new InvalidDataException("Checkpoint workload or kernel ABI does not match this run.");
        if (!string.Equals(
                checkpoint.WorkloadImplementationSha256,
                workloadImplementationSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checkpoint workload implementation does not match this run.");
        string? unknown = checkpoint.CompletedCandidates.Keys.FirstOrDefault(id => !candidateIds.Contains(id));
        if (unknown is not null)
            throw new InvalidDataException(
                $"Checkpoint contains candidate '{unknown}' outside the current parameter space.");
        return checkpoint;
    }

    public static void Write(string path, TuningCheckpoint checkpoint)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + $".tmp-{Environment.ProcessId}";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(checkpoint, JsonDefaults.Options),
            new UTF8Encoding(false));
        File.Move(temporaryPath, fullPath, overwrite: true);
    }
}
