using System.Text.Json;

namespace HlslPerf.Core;

public static class PairedProfile
{
    public static TuningProfile? Create(TuningRunReport report)
    {
        PairedRunEvidence? evidence = report.PairedEvidence;
        if (evidence is null || evidence.Protocol != PairedProtocol.Id || !evidence.Deployable ||
            !evidence.IndependentInputs || !evidence.Confirmation.Passed || evidence.Rejections.Count != 0 ||
            report.Selection?.CandidateId != evidence.SelectedAfterCalibration ||
            !PairedProtocol.IndependentInputs(evidence.Observations, evidence.SelectedAfterCalibration, report.BaselineCandidateId)) return null;
        PairedSlot[] expected = PairedProtocol.Schedule(evidence.Options, "confirmation",
            [evidence.SelectedAfterCalibration], report.BaselineCandidateId).ToArray();
        PairedObservation[] rows = evidence.Observations.Where(o => o.Slot.Phase == "confirmation").ToArray();
        if (!rows.Select(o => o.Slot).SequenceEqual(expected) ||
            rows.Any(o => o.StartedUtc < evidence.SelectionLockedUtc || o.FinishedUtc < o.StartedUtc ||
                !o.Result.Compiled || o.Result.Error is not null || o.Result.Correctness?.Passed != true ||
                o.Result.SamplesMilliseconds.Count != 1)) return null;
        if (PairedProtocol.Select(evidence.Calibration, report.BaselineCandidateId) != evidence.SelectedAfterCalibration)
            return null;
        string selectionLock = ContentHash.Sha256(JsonSerializer.Serialize(new
        {
            identity = evidence.ExecutionIdentitySha256, session = evidence.SessionId,
            selected = evidence.SelectedAfterCalibration, calibration = evidence.Calibration, lockedUtc = evidence.SelectionLockedUtc
        }, JsonDefaults.Options));
        if (selectionLock != evidence.SelectionLockSha256) return null;
        PairedComparison replay = PairedProtocol.Compare(evidence.Observations, "confirmation", evidence.SelectedAfterCalibration,
            evidence.Options, evidence.MinimumRequiredSpeedup, evidence.MaximumCoefficientOfVariation);
        if (!replay.Passed || JsonSerializer.Serialize(replay, JsonDefaults.Options) !=
            JsonSerializer.Serialize(evidence.Confirmation, JsonDefaults.Options)) return null;
        string selected = evidence.SelectedAfterCalibration;
        CandidateResult? candidate = report.Candidates.FirstOrDefault(c => c.CandidateId == selected);
        if (candidate is null) return null;
        double[] samples = rows.Where(o => !o.Slot.IsBaseline).SelectMany(o => o.Result.SamplesMilliseconds).ToArray();
        if (samples.Any(x => !double.IsFinite(x) || x <= 0)) return null;
        DistributionSummary timing = StableStatistics.Summarize(samples);
        string confirmationHash = ContentHash.Sha256(JsonSerializer.Serialize(new
        {
            evidence.SelectionLockSha256, evidence.ExecutionIdentitySha256, evidence.Confirmation, rows
        }, JsonDefaults.Options));
        string compatibility = ContentHash.Sha256(JsonSerializer.Serialize(new
        {
            source = ContentHash.CompatibilityKey(report.Device, report.ManifestSha256, report.KernelSha256),
            report.WorkloadId, report.KernelAbiVersion, evidence.WorkloadImplementationSha256,
            evidence.ExecutionIdentitySha256, protocol = PairedProtocol.Id, confirmationHash
        }, JsonDefaults.Options));
        double? throughput = rows.First(o => !o.Slot.IsBaseline).Result.ThroughputMillionItemsPerSecond;
        if (throughput is null) return null;
        // Recover logical item count from the verified single observation, then apply confirmation median.
        CandidateResult first = rows.First(o => !o.Slot.IsBaseline).Result;
        double itemsMillions = throughput.Value * first.SamplesMilliseconds[0] / 1000;
        return new("3.0", report.FinishedUtc, compatibility, report.Device, report.ManifestSha256,
            report.KernelSha256, selected, candidate.Defines, timing.MedianMilliseconds,
            timing.P95Milliseconds, itemsMillions / (timing.MedianMilliseconds / 1000),
            evidence.Confirmation.GeometricMeanSpeedup, report.WorkloadId, report.KernelAbiVersion,
            candidate.Defines.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ProfileDefine(p.Key, p.Value)).ToArray(),
            PairedProtocol.Id, "independently-confirmed", evidence.WorkloadImplementationSha256,
            evidence.ExecutionIdentitySha256, confirmationHash);
    }
}
