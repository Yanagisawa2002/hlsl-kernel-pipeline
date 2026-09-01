namespace HlslPerf.Core;

public static class CandidateGenerator
{
    public static IReadOnlyList<KernelCandidate> Expand(TuningManifest manifest, int maximumCandidates = 512)
    {
        manifest.Validate();
        long count = manifest.Axes.Aggregate(1L, (total, axis) => checked(total * axis.Values.Count));
        if (count > maximumCandidates)
            throw new InvalidDataException($"Candidate space contains {count} variants; the v0.1 safety limit is {maximumCandidates}.");

        List<Dictionary<string, int>> partial = [new Dictionary<string, int>(manifest.FixedDefines, StringComparer.Ordinal)];
        foreach (CandidateAxis axis in manifest.Axes)
        {
            List<Dictionary<string, int>> next = [];
            foreach (Dictionary<string, int> current in partial)
                foreach (int value in axis.Values)
                {
                    Dictionary<string, int> expanded = new(current, StringComparer.Ordinal) { [axis.Name] = value };
                    next.Add(expanded);
                }
            partial = next;
        }

        return partial.Select(values => new KernelCandidate(values)).ToArray();
    }

    public static KernelCandidate ResolveBaseline(TuningManifest manifest, IReadOnlyList<KernelCandidate> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        if (manifest.BaselineDefines.Count == 0)
            return candidates[0];

        return candidates.FirstOrDefault(candidate => manifest.BaselineDefines.All(pair =>
                   candidate.Defines.TryGetValue(pair.Key, out int value) && value == pair.Value))
            ?? throw new InvalidDataException("The declared baseline does not resolve to a generated candidate.");
    }
}

public static class StableStatistics
{
    public static DistributionSummary Summarize(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0 || samples.Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentException("Samples must contain finite, non-negative values.", nameof(samples));

        double[] sorted = samples.Order().ToArray();
        double mean = samples.Average();
        double variance = samples.Count == 1 ? 0 : samples.Sum(value => Math.Pow(value - mean, 2)) / (samples.Count - 1);
        double standardDeviation = Math.Sqrt(variance);
        return new DistributionSummary(
            sorted[0],
            Percentile(sorted, 0.50),
            mean,
            Percentile(sorted, 0.95),
            sorted[^1],
            standardDeviation,
            mean == 0 ? 0 : standardDeviation / mean);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1)
            return sorted[0];
        double position = (sorted.Count - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}

public static class CandidateSelector
{
    public static SelectionResult? Select(
        IReadOnlyList<CandidateResult> results,
        string? baselineCandidateId = null,
        double minimumRequiredSpeedup = 1)
    {
        if (minimumRequiredSpeedup < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumRequiredSpeedup));
        CandidateResult[] valid = results.Where(IsValid).ToArray();
        if (valid.Length == 0)
            return null;

        CandidateResult[] stable = valid.Where(result => result.Stable).ToArray();
        IReadOnlyList<CandidateResult> pool = stable.Length > 0 ? stable : valid;
        CandidateResult winner = pool
            .OrderBy(result => result.Timing!.MedianMilliseconds)
            .ThenBy(result => result.Timing!.P95Milliseconds)
            .ThenBy(result => result.CandidateId, StringComparer.Ordinal)
            .First();
        bool usedStablePool = stable.Length > 0;
        string poolReason = usedStablePool
            ? "Lowest median GPU time among correctness-passing candidates inside the stability budget; p95 is the tie-breaker."
            : "No candidate met the stability budget; selected the lowest median among correctness-passing candidates and flagged the run.";

        CandidateResult? baseline = baselineCandidateId is null
            ? null
            : valid.FirstOrDefault(result => result.CandidateId == baselineCandidateId);
        if (baseline is null || !baseline.Stable || winner.CandidateId == baseline.CandidateId)
            return new SelectionResult(winner.CandidateId, winner.CandidateId, usedStablePool, false, poolReason);

        double observedSpeedup = baseline.Timing!.MedianMilliseconds / winner.Timing!.MedianMilliseconds;
        bool clearsMedianGuard = observedSpeedup >= minimumRequiredSpeedup;
        bool clearsTailGuard = winner.Timing.P95Milliseconds <= baseline.Timing.P95Milliseconds;
        if (clearsMedianGuard && clearsTailGuard)
        {
            return new SelectionResult(
                winner.CandidateId,
                winner.CandidateId,
                usedStablePool,
                false,
                $"{poolReason} The candidate cleared the {minimumRequiredSpeedup:0.###}× median guard and did not regress p95.");
        }

        string failedGuard = !clearsMedianGuard && !clearsTailGuard
            ? "median improvement and p95 non-regression guards"
            : !clearsMedianGuard ? "median improvement guard" : "p95 non-regression guard";
        return new SelectionResult(
            baseline.CandidateId,
            winner.CandidateId,
            usedStablePool,
            true,
            $"Retained the declared baseline because the observed fastest candidate did not clear the {failedGuard} " +
            $"(observed {observedSpeedup:0.####}×; required {minimumRequiredSpeedup:0.###}×).");
    }

    private static bool IsValid(CandidateResult result) =>
        result.Compiled && result.Error is null && result.Correctness?.Passed == true && result.Timing is not null;
}
