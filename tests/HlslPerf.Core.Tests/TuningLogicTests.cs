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
