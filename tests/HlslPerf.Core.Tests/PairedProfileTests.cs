using System.Text.Json;
using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class PairedProfileTests
{
    [Fact]
    public void ProfileUsesConfirmationOnlyAndBindsDeploymentContents()
    {
        TuningRunReport report = Report();
        TuningProfile profile = Assert.IsType<TuningProfile>(ReportWriter.CreateProfile(report));
        Assert.Equal("3.0", profile.SchemaVersion);
        Assert.Equal(0.82, profile.MedianGpuMilliseconds);
        Assert.Equal(ContentHash.DefinesSha256(profile.Defines), profile.DefinesSha256);
        Assert.Equal("independently-confirmed", profile.EvidenceStatus);
        var tampered = report with { Candidates = report.Candidates.Select(c => c.CandidateId == "X-2"
            ? c with { Defines = new Dictionary<string, int> { ["X"] = 3 } } : c).ToArray() };
        Assert.Null(ReportWriter.CreateProfile(tampered));
        Assert.Null(ReportWriter.CreateProfile(report with { PairedEvidence = report.PairedEvidence! with { SelectionLockSha256 = "tampered" } }));
    }

    [Fact]
    public void BaselineNoBenefitStillRequiresFreshCorrectStableConfirmation()
    {
        var report = Report(baselineControl: true);
        Assert.Equal("X-1", Assert.IsType<TuningProfile>(ReportWriter.CreateProfile(report)).CandidateId);
        Assert.Null(ReportWriter.CreateProfile(report with { PairedEvidence = report.PairedEvidence! with { IndependentInputs = false } }));
    }

    [Fact]
    public void HistoricalEvidenceCannotSilentlyBecomeConfirmed()
    {
        var historical = Report() with { MeasurementProtocol = HlslPerfSdk.MeasurementProtocol, PairedEvidence = null };
        Assert.Null(ReportWriter.CreateProfile(historical));
    }

    [Fact]
    public void UnrelatedFailedCandidateRemainsVisibleWithoutBlockingConfirmedWinner()
    {
        var report = Report(includeFailedChallenger: true);
        Assert.Contains(report.PairedEvidence!.Observations, o => o.Result.Error == "synthetic failed challenger");
        Assert.Equal("X-2", Assert.IsType<TuningProfile>(ReportWriter.CreateProfile(report)).CandidateId);
    }

    [Fact]
    public void PassingEachOwnOracleDoesNotProveArmsAreComparable()
    {
        var report = Report(); var evidence = report.PairedEvidence!;
        var mismatched = evidence.Observations.Select(o => o.Slot.Phase == "confirmation" && !o.Slot.IsBaseline
            ? o with { Scenario = o.Scenario! with { Slots = o.Scenario.Slots.Select(s => s with {
                ExpectedSha256 = ContentHash.Sha256("different semantic output") }).ToArray() } } : o).ToArray();
        var comparison = PairedProtocol.Compare(mismatched, "confirmation", "X-2", evidence.Options, 1.01, 0.05);
        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Rejections, r => r.Contains("incompatible primary oracles"));
    }

    [Fact]
    public void CompleteCheckpointReplaysFrozenEvidenceAndRejectsWrongSession()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hlslperf-profile-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "checkpoint.json");
        try
        {
            var report = Report(); var evidence = report.PairedEvidence!;
            var checkpoint = new PairedCheckpoint(PairedProtocol.CheckpointSchema, PairedProtocol.Id,
                evidence.ExecutionIdentitySha256, evidence.SessionId, evidence.Observations,
                evidence.SelectedAfterCalibration, evidence.SelectionLockSha256, report, []);
            PairedCheckpointStore.Write(path, checkpoint);
            var loaded = PairedCheckpointStore.Load(path, evidence.ExecutionIdentitySha256);
            Assert.NotNull(ReportWriter.CreateProfile(loaded.CompletedReport!));
            Assert.Equal(evidence.Observations.Count, loaded.Observations.Count);
            PairedCheckpointStore.Write(path, checkpoint with { SessionId = "other-session" });
            Assert.Throws<InvalidDataException>(() => PairedCheckpointStore.Load(path, evidence.ExecutionIdentitySha256));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static TuningRunReport Report(bool baselineControl = false, bool includeFailedChallenger = false)
    {
        var options = new PairedMeasurementOptions { CalibrationBlocks = 6, ConfirmationBlocks = 6, ResidentSlots = 3 };
        var device = new DeviceFingerprint("Synthetic", 1, 2, 3, 4, "luid", "driver", "D3D12", "6_0", "compiler", "os");
        List<PairedObservation> rows = [];
        DateTimeOffset lockedUtc = DateTimeOffset.UnixEpoch.AddSeconds(2);
        Add("calibration", "X-2", baselineControl ? 1.02 : 0.8);
        var calibration = (includeFailedChallenger ? new[] { "X-2", "X-3" } : new[] { "X-2" })
            .Select(id => PairedProtocol.Compare(rows, "calibration", id, options, 1.01, 0.05)).ToArray();
        string selected = PairedProtocol.Select(calibration, "X-1");
        string identity = ContentHash.Sha256("identity"), session = "synthetic-test";
        string selectionLock = ContentHash.Sha256(JsonSerializer.Serialize(new { identity, session, selected, calibration, lockedUtc }, JsonDefaults.Options));
        Add("confirmation", selected, baselineControl ? 1.005 : 0.82);
        var confirmation = PairedProtocol.Compare(rows, "confirmation", selected, options, 1.01, 0.05);
        var evidence = new PairedRunEvidence(PairedProtocol.Id, session, options, identity, ContentHash.Sha256("workload"),
            1.01, 0.05, selected, selectionLock, lockedUtc, calibration, confirmation, rows, true, true, [], []);
        return new("3.0", DateTimeOffset.UnixEpoch, lockedUtc.AddSeconds(5), "manifest", ContentHash.Sha256("manifest"),
            ContentHash.Sha256("source"), device, "X-1", new(selected, selected, true, baselineControl, "test"),
            rows.Where(o => o.Slot.Phase == "calibration").Select(o => o.Result).DistinctBy(r => r.CandidateId).ToArray(),
            "test", KernelAbiV1.Id, null, PairedProtocol.Id, evidence);

        void Add(string phase, string challenger, double time)
        {
            foreach (PairedSlot slot in PairedProtocol.Schedule(options, phase,
                phase == "calibration" && includeFailedChallenger ? [challenger, "X-3"] : [challenger], "X-1"))
            {
                double ms = slot.IsBaseline ? 1 : time;
                var defines = new Dictionary<string, int> { ["X"] = slot.CandidateId == "X-1" ? 1 : 2 };
                var correct = new CorrectnessResult(true, ContentHash.Sha256("oracle"), ContentHash.Sha256("oracle"), "synthetic");
                var result = new CandidateResult(slot.CandidateId, defines, true, null, correct, [ms], 8,
                    StableStatistics.Summarize([ms]), 1 / ms, true, null);
                if (slot.CandidateId == "X-3") result = result with { Compiled = false, Error = "synthetic failed challenger" };
                var ring = Enumerable.Range(0, options.ResidentSlots).Select(i => new ScenarioSlotEvidence(i,
                    slot.InputSeed + i, ContentHash.Sha256("input-" + (slot.InputSeed + i)), correct.ExpectedSha256, 128, 1)).ToArray();
                var memory = new ScenarioMemorySnapshot(DateTimeOffset.UnixEpoch, null, null, "synthetic");
                var scenario = new ScenarioSessionEvidence(WorkloadScenario.Schema, "synthetic", "synthetic", "identity", device,
                    ContentHash.Sha256("source"), ContentHash.Sha256("workload"), new Dictionary<string, string>(), ring,
                    384, 384, 384, 128, "synthetic", memory, memory, "synthetic", "synthetic", "synthetic");
                var verification = ring.Select(s => new ScenarioVerification(s.Slot, s.InputSeed, "output", correct)).ToArray();
                var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(phase == "calibration" ? 1 : 3);
                rows.Add(new(slot, timestamp, timestamp, ContentHash.Sha256(string.Join("/", ring.Select(s => s.InputSha256))),
                    result, slot.CandidateId == "X-3" ? null : scenario, slot.CandidateId == "X-3" ? null : verification));
            }
        }
    }
}
