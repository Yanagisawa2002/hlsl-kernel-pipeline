using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class TuningLogicTests
{
    [Fact]
    public void ExpandProducesCartesianProductWithFixedDefines()
    {
        TuningManifest manifest = CreateManifest();
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);

        Assert.Equal(4, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(64, candidate.GetRequired("ROUNDS")));
        Assert.Equal(4, candidates.Select(candidate => candidate.Id).Distinct().Count());
    }

    [Fact]
    public void ExpandAndResolveBaselineIncludeAlgorithmBackendAxis()
    {
        TuningManifest manifest = new()
        {
            Name = "scan-backends",
            KernelPath = "scan.hlsl",
            WorkItemCount = 1,
            MeasurementBatches = 3,
            ThreadsPerGroupParameter = "GROUP",
            ElementsPerThreadParameter = "EPT",
            BaselineDefines = new Dictionary<string, int>
            {
                ["GROUP"] = 256,
                ["EPT"] = 4,
                ["BACKEND"] = 1
            },
            Axes =
            [
                new CandidateAxis { Name = "GROUP", Values = [128, 256] },
                new CandidateAxis { Name = "EPT", Values = [1, 4] },
                new CandidateAxis { Name = "BACKEND", Values = [1, 2] }
            ]
        };

        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        KernelCandidate baseline = CandidateGenerator.ResolveBaseline(manifest, candidates);

        Assert.Equal(8, candidates.Count);
        Assert.Equal(1, baseline.GetRequired("BACKEND"));
        Assert.Equal(256, baseline.GetRequired("GROUP"));
        Assert.Equal(4, baseline.GetRequired("EPT"));
    }

    [Fact]
    public void SchemaThreeConditionalAxesAvoidMeaninglessBackendProducts()
    {
        TuningManifest manifest = new()
        {
            SchemaVersion = "3.0",
            Name = "conditional",
            KernelPath = "scan.hlsl",
            WorkItemCount = 1,
            MeasurementBatches = 3,
            Workload = new WorkloadSpec
            {
                Id = "exclusive-scan-u32-v1",
                Parameters = new Dictionary<string, long> { ["elementCount"] = 1 }
            },
            Axes =
            [
                new CandidateAxis { Name = "BACKEND", Values = [1, 3] },
                new CandidateAxis
                {
                    Name = "PERSISTENT_GROUPS",
                    Values = [64, 128],
                    When = new Dictionary<string, IReadOnlyList<int>> { ["BACKEND"] = [3] }
                }
            ]
        };

        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);

        Assert.Equal(3, candidates.Count);
        Assert.Single(candidates, candidate => candidate.GetRequired("BACKEND") == 1);
        Assert.DoesNotContain(
            candidates.Single(candidate => candidate.GetRequired("BACKEND") == 1).Defines.Keys,
            name => name == "PERSISTENT_GROUPS");
    }

    [Fact]
    public void SchemaThreeConstraintsFilterInvalidCombinations()
    {
        TuningManifest manifest = new()
        {
            SchemaVersion = "3.0",
            Name = "constraints",
            KernelPath = "scan.hlsl",
            WorkItemCount = 1,
            MeasurementBatches = 3,
            Workload = new WorkloadSpec
            {
                Id = "exclusive-scan-u32-v1",
                Parameters = new Dictionary<string, long> { ["elementCount"] = 1 }
            },
            Axes =
            [
                new CandidateAxis { Name = "BACKEND", Values = [1, 3] },
                new CandidateAxis { Name = "GROUP", Values = [128, 512] }
            ],
            Constraints =
            [
                new CandidateConstraint
                {
                    If = new Dictionary<string, IReadOnlyList<int>> { ["BACKEND"] = [3] },
                    Then = new Dictionary<string, IReadOnlyList<int>> { ["GROUP"] = [512] }
                }
            ]
        };

        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);

        Assert.Equal(3, candidates.Count);
        Assert.DoesNotContain(candidates, candidate =>
            candidate.GetRequired("BACKEND") == 3 && candidate.GetRequired("GROUP") == 128);
    }

    [Fact]
    public void SummaryUsesMedianAndInterpolatedP95()
    {
        DistributionSummary summary = StableStatistics.Summarize([1, 2, 3, 4, 5]);

        Assert.Equal(3, summary.MedianMilliseconds);
        Assert.Equal(4.8, summary.P95Milliseconds, 10);
        Assert.True(summary.CoefficientOfVariation > 0);
    }

    [Fact]
    public void SelectorRejectsIncorrectFastCandidate()
    {
        CandidateResult incorrect = Result("fast-wrong", 0.1, stable: true, passed: false);
        CandidateResult correct = Result("slower-correct", 0.2, stable: true, passed: true);

        SelectionResult? selection = CandidateSelector.Select([incorrect, correct]);

        Assert.NotNull(selection);
        Assert.Equal("slower-correct", selection.CandidateId);
    }

    [Fact]
    public void SelectorRetainsBaselineWhenGainIsBelowDeploymentGuard()
    {
        CandidateResult baseline = Result("baseline", 1.0, stable: true, passed: true);
        CandidateResult observedFastest = Result("observed-fastest", 0.995, stable: true, passed: true);

        SelectionResult? selection = CandidateSelector.Select(
            [baseline, observedFastest],
            baseline.CandidateId,
            minimumRequiredSpeedup: 1.01);

        Assert.NotNull(selection);
        Assert.Equal("baseline", selection.CandidateId);
        Assert.Equal("observed-fastest", selection.ObservedFastestCandidateId);
        Assert.True(selection.RetainedBaseline);
    }

    [Fact]
    public void SelectorRejectsMedianWinWhenP95Regresses()
    {
        CandidateResult baseline = Result("baseline", 1.0, stable: true, passed: true, p95: 1.05);
        CandidateResult risky = Result("risky", 0.9, stable: true, passed: true, p95: 1.10);

        SelectionResult? selection = CandidateSelector.Select(
            [baseline, risky],
            baseline.CandidateId,
            minimumRequiredSpeedup: 1.01);

        Assert.NotNull(selection);
        Assert.Equal("baseline", selection.CandidateId);
        Assert.True(selection.RetainedBaseline);
    }

    [Fact]
    public void SelectorMarksRunNonDeployableWhenDeclaredBaselineIsNoisy()
    {
        CandidateResult baseline = Result("baseline", 1.0, stable: false, passed: true);
        CandidateResult winner = Result("winner", 0.5, stable: true, passed: true);

        SelectionResult? selection = CandidateSelector.Select(
            [baseline, winner],
            baseline.CandidateId,
            minimumRequiredSpeedup: 1.01);

        Assert.NotNull(selection);
        Assert.Equal("winner", selection.CandidateId);
        Assert.False(selection.UsedStablePool);
        Assert.Contains("observational only", selection.Reason);
    }

    [Fact]
    public void ReportWriterNeverEmitsProfileFromNoisyFallbackPool()
    {
        CandidateResult candidate = Result("only-correct-but-noisy", 1.0, stable: false, passed: true);
        TuningRunReport report = new(
            "2.0",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "manifest.json",
            new string('a', 64),
            new string('b', 64),
            new DeviceFingerprint("gpu", 1, 2, 3, 4, "5", "driver", "D3D12", "6_6", "DXC", "OS"),
            candidate.CandidateId,
            new SelectionResult(
                candidate.CandidateId,
                candidate.CandidateId,
                UsedStablePool: false,
                RetainedBaseline: false,
                "Only the noisy fallback pool was available."),
            [candidate],
            "test-v1",
            KernelAbiV1.Id);

        Assert.Null(ReportWriter.CreateProfile(report));
    }

    [Fact]
    public void ReportWriterRevalidatesSelectedCandidateBeforeEmittingProfile()
    {
        CandidateResult invalid = Result("invalid", 1.0, stable: true, passed: false);
        TuningRunReport report = ReportWithSelection(invalid, usedStablePool: true);

        Assert.Null(ReportWriter.CreateProfile(report));
    }

    [Fact]
    public void WritingNonDeployableRunRemovesStaleProfile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hlslperf-report-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string staleProfile = Path.Combine(directory, "profile.json");
            File.WriteAllText(staleProfile, "stale");
            CandidateResult noisy = Result("noisy", 1.0, stable: false, passed: true);

            ReportArtifacts artifacts = ReportWriter.Write(
                ReportWithSelection(noisy, usedStablePool: false),
                directory);

            Assert.Null(artifacts.ProfileJsonPath);
            Assert.False(File.Exists(staleProfile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TuningRunReport ReportWithSelection(CandidateResult candidate, bool usedStablePool) => new(
        "2.0",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "manifest.json",
        new string('a', 64),
        new string('b', 64),
        new DeviceFingerprint("gpu", 1, 2, 3, 4, "5", "driver", "D3D12", "6_6", "DXC", "OS"),
        candidate.CandidateId,
        new SelectionResult(
            candidate.CandidateId,
            candidate.CandidateId,
            usedStablePool,
            RetainedBaseline: false,
            "test"),
        [candidate],
        "test-v1",
        KernelAbiV1.Id);

    private static CandidateResult Result(string id, double median, bool stable, bool passed, double? p95 = null)
    {
        DistributionSummary timing = new(median, median, median, p95 ?? median, p95 ?? median, 0, 0);
        return new CandidateResult(id, new Dictionary<string, int>(), true, null,
            new CorrectnessResult(passed, "a", passed ? "a" : "b", "test"), [median], 8, timing, 1, stable, null);
    }

    private static TuningManifest CreateManifest() => new()
    {
        Name = "test",
        KernelPath = "test.hlsl",
        WorkItemCount = 1,
        MeasurementBatches = 3,
        ThreadsPerGroupParameter = "GROUP",
        ElementsPerThreadParameter = "EPT",
        FixedDefines = new Dictionary<string, int> { ["ROUNDS"] = 64 },
        Axes =
        [
            new CandidateAxis { Name = "GROUP", Values = [64, 128] },
            new CandidateAxis { Name = "EPT", Values = [1, 2] }
        ]
    };
}
