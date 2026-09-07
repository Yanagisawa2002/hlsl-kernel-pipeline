using System.Text.Json;
using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class PairedProtocolTests
{
    private static readonly PairedMeasurementOptions Options = new() { CalibrationBlocks = 6, ConfirmationBlocks = 6 };

    [Fact]
    public void ScheduleIsReproducibleBalancedAndSeedDisjoint()
    {
        var a = PairedProtocol.Schedule(Options, "calibration", ["b", "c"], "a");
        Assert.Equal(a, PairedProtocol.Schedule(Options, "calibration", ["c", "b"], "a"));
        Assert.Equal(48, a.Count);
        Assert.All(a.GroupBy(s => (s.Block, s.ChallengerId)), g => Assert.Equal(2, g.Count(s => s.IsBaseline)));
        var b = PairedProtocol.Schedule(Options, "confirmation", ["b"], "a");
        Assert.Empty(a.Select(s => s.InputSeed).Intersect(b.Select(s => s.InputSeed)));
        Assert.Throws<ArgumentException>(() => PairedProtocol.Schedule(Options, "confirmation", ["b", "c"], "a"));
        Assert.Throws<InvalidDataException>(() => (Options with { ConfirmationSeedStart = Options.CalibrationSeedStart }).Validate());
    }

    [Fact]
    public void ClearBenefitPassesAndNoBenefitDoesNot()
    {
        Assert.True(Compare(Rows(0.8)).Passed);
        Assert.False(Compare(Rows(1)).Passed);
        var control = Rows(1.005, baselineControl: true);
        Assert.True(PairedProtocol.Compare(control, "calibration", "a", Options, 1.01, 0.05).Passed);
    }

    [Fact]
    public void DriftNoiseFailuresAndMissingRowsAreRetainedAndRejected()
    {
        var drift = Rows(0.8).Select(o => o.Slot.Block == 5 ? Scale(o, 1.5) : o).ToArray();
        Assert.Contains(Compare(drift).Rejections, s => s.Contains("drift"));
        var noise = Rows(0.8).Select((o, i) => i == 0 ? Scale(o, 2) : o).ToArray();
        Assert.False(Compare(noise).Passed);
        var failed = Rows(0.8).ToArray();
        failed[3] = failed[3] with { Result = failed[3].Result with { Error = "device failure" } };
        Assert.False(Compare(failed).Passed);
        Assert.Equal(24, failed.Length);
        Assert.False(Compare(failed.Skip(1).ToArray()).Passed);
    }

    [Fact]
    public void PairedIntervalRejectsUncertainApparentImprovement()
    {
        var rows = Rows(0.99).Select(o => !o.Slot.IsBaseline ? Scale(o, o.Slot.Block % 2 == 0 ? 0.92 : 1.08) : o).ToArray();
        var result = PairedProtocol.Compare(rows, "calibration", "b", Options, 1.01, 0.5);
        Assert.False(result.Passed);
        Assert.True(result.Lower95Speedup < 1.01);
    }

    [Fact]
    public void CorruptedIdentityOrderAndInputDigestAreRejected()
    {
        var rows = Rows(0.8).ToArray();
        rows[0] = rows[0] with { Result = rows[0].Result with { CandidateId = "wrong" } };
        Assert.False(Compare(rows).Passed);
        rows = Rows(0.8).ToArray();
        rows[0] = rows[0] with { Slot = rows[0].Slot with { InputSeed = 7 } };
        Assert.False(Compare(rows).Passed);
        rows = Rows(0.8).ToArray();
        rows[0] = rows[0] with { InputSha256 = "wrong" };
        Assert.False(Compare(rows).Passed);
    }

    [Fact]
    public void ConfirmationCannotSelectAnotherCandidateOrReuseCalibrationInputs()
    {
        var calibration = Rows(0.8);
        var confirmation = Rows(1.1, "confirmation");
        var selected = PairedProtocol.Select([Compare(calibration)], "a");
        Assert.Equal("b", selected);
        Assert.False(PairedProtocol.Compare(confirmation, "confirmation", selected, Options, 1.01, 0.05).Passed);
        var all = calibration.Concat(confirmation).ToArray();
        Assert.True(PairedProtocol.IndependentInputs(all, selected, "a"));
        var fake = all.Select(o => o with { InputSha256 = "unchanged" }).ToArray();
        Assert.False(PairedProtocol.IndependentInputs(fake, selected, "a"));
        Assert.Equal("b", selected);
    }

    [Fact]
    public void InterruptedCheckpointIsArchivedWithoutReusingSamplesAndRejectsIdentityDrift()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hlslperf-paired-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "checkpoint.json");
        try
        {
            var checkpoint = new PairedCheckpoint(PairedProtocol.CheckpointSchema, PairedProtocol.Id, "identity", "session",
                Rows(0.8).Take(3).ToArray(), null, null, null, []);
            PairedCheckpointStore.Write(path, checkpoint);
            var loaded = PairedCheckpointStore.Load(path, "identity");
            Assert.Equal(3, loaded.Observations.Count);
            string archive = PairedCheckpointStore.ArchiveInterrupted(path, loaded);
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(archive));
            Assert.Throws<InvalidDataException>(() => PairedCheckpointStore.Load(path, "different-binary"));
            PairedCheckpointStore.Write(path, checkpoint with { Protocol = HlslPerfSdk.MeasurementProtocol });
            Assert.Throws<InvalidDataException>(() => PairedCheckpointStore.Load(path, "identity"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    private static PairedComparison Compare(IReadOnlyList<PairedObservation> rows) =>
        PairedProtocol.Compare(rows, "calibration", "b", Options, 1.01, 0.05);

    private static IReadOnlyList<PairedObservation> Rows(double b, string phase = "calibration", bool baselineControl = false) =>
        PairedProtocol.Schedule(Options, phase, [baselineControl ? "a" : "b"], "a").Select(s =>
        {
            double time = s.IsBaseline ? 1 : b;
            var result = new CandidateResult(s.CandidateId, new Dictionary<string, int>(), true, null,
                new(true, "hash", "hash", "oracle"), [time], 8, StableStatistics.Summarize([time]), 1, true, null);
            return new PairedObservation(s, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                $"{s.InputSeed}/{s.CandidateId}", result);
        }).ToArray();

    private static PairedObservation Scale(PairedObservation row, double scale) => row with
    { Result = row.Result with { SamplesMilliseconds = [row.Result.SamplesMilliseconds[0] * scale] } };
}
