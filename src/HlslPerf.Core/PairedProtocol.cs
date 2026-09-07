using System.Text.Json;
using System.Text.Json.Nodes;

namespace HlslPerf.Core;

public sealed record PairedMeasurementOptions
{
    public int CalibrationBlocks { get; init; } = 8;
    public int ConfirmationBlocks { get; init; } = 8;
    public int OrderSeed { get; init; } = 73019;
    public int CalibrationSeedStart { get; init; } = 110001;
    public int ConfirmationSeedStart { get; init; } = 910001;
    public int ResidentSlots { get; init; } = 3;
    public long MaximumAllocationBytesPerArm { get; init; } = 512L * 1024 * 1024;
    public double MaximumBaselineDrift { get; init; } = 0.15;

    public void Validate()
    {
        if (CalibrationBlocks is < 6 or > 128 || ConfirmationBlocks is < 6 or > 128)
            throw new InvalidDataException("Paired phases require 6..128 complete blocks.");
        if (ResidentSlots is < 1 or > 32 || MaximumAllocationBytesPerArm is <= 0 or > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Resident ring requires 1..32 slots and a bounded per-arm allocation cap.");
        if (CalibrationSeedStart <= 0 || ConfirmationSeedStart <= 0 ||
            (long)CalibrationSeedStart + CalibrationBlocks * ResidentSlots > int.MaxValue ||
            (long)ConfirmationSeedStart + ConfirmationBlocks * ResidentSlots > int.MaxValue ||
            Enumerable.Range(CalibrationSeedStart, CalibrationBlocks * ResidentSlots)
                .Intersect(Enumerable.Range(ConfirmationSeedStart, ConfirmationBlocks * ResidentSlots)).Any())
            throw new InvalidDataException("Calibration and confirmation input seeds must be positive, bounded and disjoint.");
        if (!double.IsFinite(MaximumBaselineDrift) || MaximumBaselineDrift is <= 0 or > 1)
            throw new InvalidDataException("Maximum baseline drift must be in (0, 1].");
    }
}

public sealed record PairedSlot(string Phase, int Block, int Position, string ChallengerId,
    string CandidateId, bool IsBaseline, string Order, int OrderSeed, int InputSeed);

public sealed record PairedObservation(PairedSlot Slot, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    string? InputSha256, CandidateResult Result, ScenarioSessionEvidence? Scenario = null,
    IReadOnlyList<ScenarioVerification>? SlotVerifications = null);

public sealed record PairedComparison(string CandidateId, int Blocks, double? GeometricMeanSpeedup,
    double? Lower95Speedup, double? Upper95Speedup, double? BaselineDrift,
    bool Passed, IReadOnlyList<string> Rejections);

public sealed record PairedRunEvidence(string Protocol, string SessionId, PairedMeasurementOptions Options,
    string ExecutionIdentitySha256, string WorkloadImplementationSha256,
    double MinimumRequiredSpeedup, double MaximumCoefficientOfVariation,
    string SelectedAfterCalibration, string SelectionLockSha256, DateTimeOffset SelectionLockedUtc,
    IReadOnlyList<PairedComparison> Calibration, PairedComparison Confirmation,
    IReadOnlyList<PairedObservation> Observations, bool IndependentInputs, bool Deployable,
    IReadOnlyList<string> Rejections, IReadOnlyList<string> HistoricalAttemptPaths);

public static class PairedProtocol
{
    public const string Id = "gpu-paired-abba-independent-confirmation-v2";
    public const string CheckpointSchema = "hlslperf.paired-checkpoint.v2";

    // SHA ordering defines the permutation without depending on System.Random/runtime versions.
    public static IReadOnlyList<PairedSlot> Schedule(PairedMeasurementOptions options, string phase,
        IReadOnlyList<string> challengerIds, string baselineId)
    {
        options.Validate();
        bool confirmation = phase == "confirmation";
        if (!confirmation && phase != "calibration") throw new ArgumentException("Unknown phase.", nameof(phase));
        if (challengerIds.Count == 0 || challengerIds.Distinct(StringComparer.Ordinal).Count() != challengerIds.Count ||
            (confirmation && challengerIds.Count != 1))
            throw new ArgumentException("Confirmation must contain exactly the frozen selection.", nameof(challengerIds));
        List<PairedSlot> slots = [];
        int count = confirmation ? options.ConfirmationBlocks : options.CalibrationBlocks;
        int seedStart = confirmation ? options.ConfirmationSeedStart : options.CalibrationSeedStart;
        for (int block = 0; block < count; block++)
        {
            int position = 0;
            foreach (string id in challengerIds.OrderBy(id => ContentHash.Sha256($"{options.OrderSeed}/{phase}/{block}/{id}"), StringComparer.Ordinal))
            {
                bool abba = (Convert.ToInt32(ContentHash.Sha256($"order/{options.OrderSeed}/{phase}/{block}/{id}")[..2], 16) & 1) == 0;
                for (int offset = 0; offset < 4; offset++)
                {
                    bool isBaseline = (offset is 0 or 3) == abba;
                    slots.Add(new(phase, block, position++, id, isBaseline ? baselineId : id,
                        isBaseline, abba ? "ABBA" : "BAAB", options.OrderSeed, seedStart + block * options.ResidentSlots));
                }
            }
        }
        return slots;
    }

    public static TuningManifest WithInputSeed(TuningManifest manifest, int seed, bool fixedBatch = false)
    {
        JsonObject json = JsonSerializer.SerializeToNode(manifest, JsonDefaults.Options)!.AsObject();
        json["correctness"]!["seed"] = seed;
        if (json["workload"] is JsonObject workload)
            workload["parameters"]!["seed"] = seed;
        if (fixedBatch) json["maximumDispatchesPerBatch"] = manifest.DispatchesPerBatch;
        return json.Deserialize<TuningManifest>(JsonDefaults.Options)!;
    }

    public static string InputDigest(KernelExecutionPlan plan) => ContentHash.Sha256(string.Join("\n",
        plan.Buffers.Where(b => b.InitialData is not null).OrderBy(b => b.Name, StringComparer.Ordinal)
            .Select(b => $"{b.Name}:{b.ByteLength}:{ContentHash.Sha256(b.InitialData!)}")));

    public static PairedComparison Compare(IReadOnlyList<PairedObservation> observations, string phase,
        string challengerId, PairedMeasurementOptions options, double minimumSpeedup, double maximumCv)
    {
        int expected = phase == "calibration" ? options.CalibrationBlocks : options.ConfirmationBlocks;
        PairedObservation[] rows = observations.Where(o => o.Slot.Phase == phase && o.Slot.ChallengerId == challengerId).ToArray();
        List<string> reasons = [];
        string[] baselineIds = rows.Where(o => o.Slot.IsBaseline).Select(o => o.Slot.CandidateId).Distinct().ToArray();
        string baselineId = baselineIds.Length == 1 ? baselineIds[0] : "";
        bool baselineControl = challengerId == baselineId;
        string[] phaseIds = observations.Where(o => o.Slot.Phase == phase).Select(o => o.Slot.ChallengerId).Distinct().ToArray();
        if (phaseIds.Length == 0 || (phase == "confirmation" && phaseIds.Length != 1))
            return new(challengerId, 0, null, null, null, null, false, ["Invalid frozen phase candidate set."]);
        PairedSlot[] schedule = Schedule(options, phase, phaseIds, baselineId).Where(s => s.ChallengerId == challengerId).ToArray();
        if (!rows.Select(o => o.Slot).SequenceEqual(schedule) || rows.Any(o => o.Slot.CandidateId != o.Result.CandidateId))
            reasons.Add("Raw slot provenance does not match the frozen schedule or result identity.");
        if (rows.Select(o => o.Result.MeasuredDispatchesPerBatch).Distinct().Count() != 1 ||
            rows.Any(o => o.Result.MeasuredDispatchesPerBatch <= 0))
            reasons.Add("Incomparable dispatch batch counts.");
        List<double> logs = [], baselines = [], candidateTimes = [], baselineTimes = [];
        double drift = 0;
        if (rows.Length != expected * 4) reasons.Add("Incomplete phase; no failed or missing block may be dropped.");
        for (int block = 0; block < expected; block++)
        {
            PairedObservation[] group = rows.Where(o => o.Slot.Block == block).OrderBy(o => o.Slot.Position).ToArray();
            if (group.Length != 4 || group.Select(o => o.Slot.Position).Distinct().Count() != 4 ||
                group.Count(o => o.Slot.IsBaseline) != 2 || group.Select(o => o.Slot.InputSeed).Distinct().Count() != 1 ||
                group.Any(o => !Valid(o)))
            {
                reasons.Add($"Block {block}: missing, failed, non-positive, or inconsistent samples.");
                continue;
            }
            bool[] roles = group.Select(o => o.Slot.IsBaseline).ToArray();
            if (group.GroupBy(o => o.Slot.IsBaseline).Any(arm =>
                    arm.Any(o => string.IsNullOrEmpty(o.InputSha256)) || arm.Select(o => o.InputSha256).Distinct().Count() != 1))
                reasons.Add($"Block {block}: repeated arm inputs do not match.");
            if (!(roles.SequenceEqual(new[] { true, false, false, true }) || roles.SequenceEqual(new[] { false, true, true, false })))
                reasons.Add($"Block {block}: invalid AB/BA positions.");
            double[] a = group.Where(o => o.Slot.IsBaseline).Select(Time).ToArray();
            double[] b = group.Where(o => !o.Slot.IsBaseline).Select(Time).ToArray();
            double localDrift = Math.Max(a[0], a[1]) / Math.Min(a[0], a[1]) - 1;
            drift = Math.Max(drift, localDrift);
            logs.Add((Math.Log(a[0] / b[0]) + Math.Log(a[1] / b[1])) / 2);
            baselines.Add(Math.Sqrt(a[0] * a[1]));
            baselineTimes.AddRange(a); candidateTimes.AddRange(b);
        }
        if (logs.Count != expected) return new(challengerId, logs.Count, null, null, null, drift, false, reasons);
        drift = Math.Max(drift, baselines.Max() / baselines.Min() - 1);
        if (drift > options.MaximumBaselineDrift) reasons.Add("Baseline drift exceeded the declared budget.");
        if (StableStatistics.Summarize(baselineTimes).CoefficientOfVariation > maximumCv ||
            StableStatistics.Summarize(candidateTimes).CoefficientOfVariation > maximumCv)
            reasons.Add("Raw timing variation exceeded the declared budget (samples retained).");
        if (!baselineControl && StableStatistics.Summarize(candidateTimes).P95Milliseconds > StableStatistics.Summarize(baselineTimes).P95Milliseconds)
            reasons.Add("Candidate p95 regressed against its paired baseline.");
        double mean = logs.Average();
        double se = Math.Sqrt(logs.Sum(x => (x - mean) * (x - mean)) / (logs.Count - 1) / logs.Count);
        double radius = Critical95(logs.Count - 1) * se;
        double lower = Math.Exp(mean - radius), upper = Math.Exp(mean + radius);
        if (!baselineControl && lower < minimumSpeedup) reasons.Add("Paired block confidence interval did not clear the speedup requirement.");
        return new(challengerId, logs.Count, Math.Exp(mean), lower, upper, drift, reasons.Count == 0, reasons);
    }

    // Two-sided Student t 95% critical values, rounded upward; df>30 uses conservative df=30.
    private static double Critical95(int df) => df switch
    {
        5 => 2.571, 6 => 2.447, 7 => 2.365, 8 => 2.307, 9 => 2.263,
        >= 10 and < 15 => 2.229, >= 15 and < 20 => 2.132,
        >= 20 and < 25 => 2.086, >= 25 and < 30 => 2.060, >= 30 => 2.043,
        _ => throw new ArgumentOutOfRangeException(nameof(df))
    };

    public static string Select(IReadOnlyList<PairedComparison> calibration, string baselineId) =>
        calibration.Where(c => c.Passed).OrderByDescending(c => c.GeometricMeanSpeedup)
            .ThenBy(c => c.CandidateId, StringComparer.Ordinal).FirstOrDefault()?.CandidateId ?? baselineId;

    public static bool IndependentInputs(IReadOnlyList<PairedObservation> observations, string selected, string baseline)
    {
        foreach (string id in new[] { selected, baseline }.Distinct(StringComparer.Ordinal))
        {
            PairedObservation[] a = observations.Where(o => o.Slot.Phase == "calibration" && o.Slot.CandidateId == id).ToArray();
            PairedObservation[] b = observations.Where(o => o.Slot.Phase == "confirmation" && o.Slot.CandidateId == id).ToArray();
            if (a.Length == 0 || b.Length == 0 || a.Concat(b).Any(o => string.IsNullOrEmpty(o.InputSha256)) ||
                a.Select(o => o.Slot.InputSeed).Intersect(b.Select(o => o.Slot.InputSeed)).Any() ||
                a.Select(o => o.InputSha256).Intersect(b.Select(o => o.InputSha256)).Any()) return false;
            if (a.Concat(b).Any(o => o.Scenario is not null) &&
                (a.Concat(b).Any(o => o.Scenario is null) ||
                 a.SelectMany(o => o.Scenario!.Slots).Select(s => s.InputSeed)
                    .Intersect(b.SelectMany(o => o.Scenario!.Slots).Select(s => s.InputSeed)).Any() ||
                 a.SelectMany(o => o.Scenario!.Slots).Select(s => s.InputSha256)
                    .Intersect(b.SelectMany(o => o.Scenario!.Slots).Select(s => s.InputSha256)).Any())) return false;
        }
        return true;
    }

    private static double Time(PairedObservation row) => row.Result.SamplesMilliseconds[0];
    private static bool Valid(PairedObservation row) => row.Result.Compiled && row.Result.Error is null &&
        row.Result.Correctness?.Passed == true && row.Result.SamplesMilliseconds.Count == 1 &&
        double.IsFinite(Time(row)) && Time(row) > 0;
}
