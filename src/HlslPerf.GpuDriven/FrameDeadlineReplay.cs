namespace HlslPerf.GpuDriven;

/// <summary>
/// Replays recorded GPU durations against a fixed application deadline. This is
/// presentation-only: it never changes or fabricates a measured sample.
/// </summary>
public static class FrameDeadlineReplay
{
    public static FrameCadenceSnapshot Replay(
        IReadOnlyList<double> samplesMilliseconds,
        double budgetMilliseconds,
        int submittedFrames,
        int sourceFrameCount)
    {
        ArgumentNullException.ThrowIfNull(samplesMilliseconds);
        if (samplesMilliseconds.Count == 0 ||
            samplesMilliseconds.Any(sample => !double.IsFinite(sample) || sample < 0))
            throw new ArgumentException("GPU samples must be finite, non-negative, and non-empty.", nameof(samplesMilliseconds));
        if (!double.IsFinite(budgetMilliseconds) || budgetMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
        if (submittedFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(submittedFrames));
        if (sourceFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrameCount));

        double availableAt = 0;
        double now = submittedFrames * budgetMilliseconds;
        int completed = 0;
        int missedDeadlines = 0;
        for (int job = 0; job < submittedFrames; ++job)
        {
            double submittedAt = job * budgetMilliseconds;
            double startsAt = Math.Max(submittedAt, availableAt);
            double duration = samplesMilliseconds[job % samplesMilliseconds.Count];
            double completesAt = startsAt + duration;
            availableAt = completesAt;
            if (completesAt <= now)
                completed++;
            if (completesAt > (job + 1) * budgetMilliseconds)
                missedDeadlines++;
        }

        int sourceFrame = Math.Max(0, completed - 1) % sourceFrameCount;
        return new FrameCadenceSnapshot(
            completed,
            submittedFrames - completed,
            missedDeadlines,
            sourceFrame,
            Math.Max(0, availableAt - now));
    }
}

public sealed record FrameCadenceSnapshot(
    int Completed,
    int Backlog,
    int MissedDeadlines,
    int SourceFrame,
    double OutstandingWorkMilliseconds);
