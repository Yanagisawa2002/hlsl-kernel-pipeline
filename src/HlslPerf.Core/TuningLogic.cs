namespace HlslPerf.Core;

public static class CandidateGenerator
{
    public static IReadOnlyList<KernelCandidate> Expand(TuningManifest manifest, int maximumCandidates = 512)
    {
        manifest.Validate();
        if (maximumCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));

        List<KernelCandidate> candidates = [];
        Dictionary<string, int> current = new(manifest.FixedDefines, StringComparer.Ordinal);
        ExpandAxis(0);
        if (candidates.Count == 0)
            throw new InvalidDataException("Candidate conditions and constraints removed every variant.");
        return candidates;

        void ExpandAxis(int axisIndex)
        {
            if (axisIndex == manifest.Axes.Count)
            {
                if (!SatisfiesConstraints(current, manifest.Constraints))
                    return;
                if (candidates.Count >= maximumCandidates)
                    throw new InvalidDataException(
                        $"Candidate space exceeds the safety limit of {maximumCandidates} variants after constraints.");
                candidates.Add(new KernelCandidate(new Dictionary<string, int>(current, StringComparer.Ordinal)));
                return;
            }

            CandidateAxis axis = manifest.Axes[axisIndex];
            if (!Matches(current, axis.When))
            {
                ExpandAxis(axisIndex + 1);
                return;
            }

            foreach (int value in axis.Values)
            {
                current[axis.Name] = value;
                ExpandAxis(axisIndex + 1);
            }
            current.Remove(axis.Name);
        }
    }

    private static bool SatisfiesConstraints(
        IReadOnlyDictionary<string, int> defines,
        IReadOnlyList<CandidateConstraint> constraints) =>
        constraints.All(constraint => !Matches(defines, constraint.If) || Matches(defines, constraint.Then));

    internal static bool Matches(
        IReadOnlyDictionary<string, int> defines,
        IReadOnlyDictionary<string, IReadOnlyList<int>> condition) =>
        condition.All(pair => defines.TryGetValue(pair.Key, out int value) && pair.Value.Contains(value));

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
            : results.FirstOrDefault(result => result.CandidateId == baselineCandidateId);
        if (baselineCandidateId is not null &&
            (baseline is null || !IsValid(baseline) || !baseline.Stable))
        {
            return new SelectionResult(
                winner.CandidateId,
                winner.CandidateId,
                UsedStablePool: false,
                RetainedBaseline: false,
                "The declared baseline did not pass correctness and stability gates; the fastest valid candidate is observational only and no deployable profile may be emitted.");
        }
        if (baseline is null || winner.CandidateId == baseline.CandidateId)
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
