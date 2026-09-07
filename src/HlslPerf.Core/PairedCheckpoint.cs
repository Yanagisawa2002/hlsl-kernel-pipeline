using System.Text.Json;

namespace HlslPerf.Core;

public sealed record PairedCheckpoint(string SchemaVersion, string Protocol, string IdentitySha256,
    string SessionId, IReadOnlyList<PairedObservation> Observations, string? SelectedCandidateId,
    string? SelectionLockSha256, TuningRunReport? CompletedReport,
    IReadOnlyList<string> HistoricalAttemptPaths);

public static class PairedCheckpointStore
{
    public static PairedCheckpoint Load(string path, string identity)
    {
        PairedCheckpoint checkpoint = JsonSerializer.Deserialize<PairedCheckpoint>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException("Empty paired checkpoint.");
        if (checkpoint.SchemaVersion != PairedProtocol.CheckpointSchema || checkpoint.Protocol != PairedProtocol.Id ||
            checkpoint.IdentitySha256 != identity)
            throw new InvalidDataException("Paired checkpoint protocol, manifest, source, workload, runner binary or device identity mismatch.");
        if (checkpoint.CompletedReport is { } report &&
            (report.MeasurementProtocol != PairedProtocol.Id || report.PairedEvidence?.SessionId != checkpoint.SessionId ||
             report.PairedEvidence.ExecutionIdentitySha256 != identity ||
             report.PairedEvidence.SelectionLockSha256 != checkpoint.SelectionLockSha256))
            throw new InvalidDataException("Completed report does not match the checkpoint session and frozen selection.");
        return checkpoint;
    }

    // Partial attempts are retained, but their timestamps never enter the next session's estimates.
    public static string ArchiveInterrupted(string path, PairedCheckpoint checkpoint)
    {
        if (checkpoint.CompletedReport is not null) throw new InvalidOperationException("A complete run should be replayed, not resampled.");
        string archive = Path.GetFullPath(path) + ".historical-" + Guid.NewGuid().ToString("N") + ".json";
        File.Copy(path, archive, overwrite: false);
        return archive;
    }

    public static void Write(string path, PairedCheckpoint checkpoint)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoint, JsonDefaults.Options));
        File.Move(temporary, full, overwrite: true);
    }
}
