using System.Text.Json;
using HlslPerf.Core;

namespace HlslPerf.D3D12;

public sealed partial class D3D12Tuner
{
    private TuningRunReport RunPaired(TuningManifest manifest, string manifestPath, IKernelWorkload workload,
        Action<TuningProgress>? progress, CancellationToken cancellationToken, string? compilerCacheDirectory,
        Action<KernelCandidate, ReadOnlyMemory<byte>>? captureVerifiedOutput, TuningCheckpointOptions? checkpointOptions)
    {
        string fullPath = Path.GetFullPath(manifestPath);
        string kernelPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, manifest.KernelPath));
        HlslSourceGraph graph = HlslSourceGraph.Load(kernelPath);
        string manifestHash = ContentHash.Sha256(File.ReadAllBytes(fullPath));
        DeviceFingerprint fingerprint = CreateFingerprint(manifest.ShaderModel);
        string workloadHash = WorkloadIdentity.Compute(workload);
        string identity = ContentHash.Sha256(JsonSerializer.Serialize(new
        {
            protocol = PairedProtocol.Id, manifestHash, effectiveManifest = manifest,
            kernelHash = graph.CombinedSha256, workloadHash, fingerprint,
            binaries = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .Where(p => Path.GetFileName(p).StartsWith("HlslPerf", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(p).StartsWith("dx", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(p).StartsWith("Vortice", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(p => new { name = Path.GetFileName(p), hash = ContentHash.Sha256(File.ReadAllBytes(p)) }).ToArray()
        }, JsonDefaults.Options));
        List<string> historical = [];
        if (checkpointOptions?.Resume == true)
        {
            PairedCheckpoint saved = PairedCheckpointStore.Load(checkpointOptions.Path, identity);
            if (saved.CompletedReport is { } complete)
                return complete with { Resume = new(true, complete.Candidates.Count, 0, PairedProtocol.CheckpointSchema) };
            historical.AddRange(saved.HistoricalAttemptPaths);
            historical.Add(PairedCheckpointStore.ArchiveInterrupted(checkpointOptions.Path, saved));
        }
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string session = Guid.NewGuid().ToString("N");
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        KernelCandidate baseline = CandidateGenerator.ResolveBaseline(manifest, candidates);
        Dictionary<string, KernelCandidate> byId = candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        string[] challengers = candidates.Where(c => c.Id != baseline.Id).Select(c => c.Id).ToArray();
        if (challengers.Length == 0) challengers = [baseline.Id];
        List<PairedObservation> observations = [];
        string? selected = null, selectionLock = null;
        Dictionary<string, CompilationSet> compilations = new(StringComparer.Ordinal);
        Save(null);
        Sample(PairedProtocol.Schedule(manifest.PairedMeasurement, "calibration", challengers, baseline.Id));
        PairedComparison[] calibration = challengers.Select(id => PairedProtocol.Compare(observations, "calibration", id,
            manifest.PairedMeasurement, manifest.MinimumRequiredSpeedup, manifest.MaximumCoefficientOfVariation)).ToArray();
        selected = PairedProtocol.Select(calibration, baseline.Id);
        DateTimeOffset lockedUtc = DateTimeOffset.UtcNow;
        selectionLock = ContentHash.Sha256(JsonSerializer.Serialize(new { identity, session, selected, calibration, lockedUtc }, JsonDefaults.Options));
        Save(null); // The chosen candidate is durably frozen BEFORE sampling confirmation.
        Sample(PairedProtocol.Schedule(manifest.PairedMeasurement, "confirmation", [selected], baseline.Id));
        PairedComparison confirmation = PairedProtocol.Compare(observations, "confirmation", selected,
            manifest.PairedMeasurement, selected == baseline.Id ? 1 : manifest.MinimumRequiredSpeedup,
            manifest.MaximumCoefficientOfVariation);
        bool independent = PairedProtocol.IndependentInputs(observations, selected, baseline.Id);
        List<string> rejections = confirmation.Rejections.ToList();
        if (observations.Any(o => o.Slot.Phase == "calibration" && o.Slot.IsBaseline &&
            (o.Result.Error is not null || o.Result.Correctness?.Passed != true)))
            rejections.Add("Calibration baseline contained failed observations.");
        if (!independent) rejections.Add("Fresh declared seeds did not produce disjoint actual calibration/confirmation inputs.");
        double[] calibrationBase = observations.Where(o => o.Slot.Phase == "calibration" && o.Slot.IsBaseline)
            .SelectMany(o => o.Result.SamplesMilliseconds).Where(double.IsFinite).Where(x => x > 0).ToArray();
        double[] confirmationBase = observations.Where(o => o.Slot.Phase == "confirmation" && o.Slot.IsBaseline)
            .SelectMany(o => o.Result.SamplesMilliseconds).Where(double.IsFinite).Where(x => x > 0).ToArray();
        if (calibrationBase.Length == 0 || confirmationBase.Length == 0 ||
            Math.Abs(StableStatistics.Summarize(confirmationBase).MedianMilliseconds /
                StableStatistics.Summarize(calibrationBase).MedianMilliseconds - 1) > manifest.PairedMeasurement.MaximumBaselineDrift)
            rejections.Add("Baseline changed between calibration and confirmation beyond the declared drift budget.");
        if (calibrationBase.Length > 0 && StableStatistics.Summarize(calibrationBase).CoefficientOfVariation > manifest.MaximumCoefficientOfVariation)
            rejections.Add("Calibration baseline exceeded the raw variation budget.");
        bool deployable = confirmation.Passed && independent && rejections.Count == 0;
        List<CandidateResult> summaries = candidates.Select(candidate => Summarize(candidate.Id)).ToList();
        // Calibration summaries remain descriptive; profile timings are taken from confirmation only.
        PairedRunEvidence evidence = new(PairedProtocol.Id, session, manifest.PairedMeasurement, identity, workloadHash,
            manifest.MinimumRequiredSpeedup, manifest.MaximumCoefficientOfVariation,
            selected, selectionLock, lockedUtc, calibration, confirmation, observations, independent, deployable, rejections, historical);
        TuningRunReport report = new("3.0", started, DateTimeOffset.UtcNow, fullPath, manifestHash,
            graph.CombinedSha256, fingerprint, baseline.Id,
            new(selected, selected, deployable, selected == baseline.Id,
                deployable ? "Frozen calibration selection passed independent paired confirmation." :
                    "No deployable profile: " + string.Join(" ", rejections)),
            summaries, workload.Id, manifest.KernelAbiVersion,
            new(checkpointOptions?.Resume == true, 0, candidates.Count, PairedProtocol.CheckpointSchema),
            PairedProtocol.Id, evidence);
        Save(report);
        return report;

        void Sample(IReadOnlyList<PairedSlot> slots)
        {
            foreach (PairedSlot slot in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset sampleStart = DateTimeOffset.UtcNow;
                KernelCandidate candidate = byId[slot.CandidateId];
                CandidateResult result;
                string? inputHash = null;
                try
                {
                    TuningManifest inputManifest = PairedProtocol.WithInputSeed(manifest, slot.InputSeed, fixedBatch: true);
                    KernelExecutionPlan plan = workload.Build(inputManifest, candidate);
                    plan.Validate();
                    if (plan.AbiVersion != manifest.KernelAbiVersion) throw new InvalidDataException("Plan ABI does not match the manifest.");
                    inputHash = PairedProtocol.InputDigest(plan);
                    string compileKey = candidate.Id + "/" + string.Join("/", plan.Passes.Select(p => p.EntryPoint).Distinct().Order());
                    if (!compilations.TryGetValue(compileKey, out CompilationSet? compilation))
                    {
                        compilation = CompilePasses(graph.RootSource, kernelPath, graph.CombinedSha256, inputManifest,
                            candidate, plan.Passes.Select(p => p.EntryPoint).Distinct(StringComparer.Ordinal),
                            compilerCacheDirectory, graph.IncludeDirectories);
                        compilations.Add(compileKey, compilation);
                    }
                    result = compilation.Success
                        ? MeasureCandidate(inputManifest, candidate, plan, compilation, captureVerifiedOutput, sampleCount: 1)
                        : Failure(candidate, false, compilation.Diagnostics, "DXC compilation failed.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = Failure(candidate, false, null, exception.ToString());
                }
                observations.Add(new(slot, sampleStart, DateTimeOffset.UtcNow, inputHash, result));
                Save(null);
                progress?.Invoke(new(observations.Count, slots.Count, candidate.Id,
                    $"{slot.Phase}/block-{slot.Block}/position-{slot.Position}", result));
            }
        }

        CandidateResult Summarize(string id)
        {
            CandidateResult[] rows = observations.Where(o => o.Slot.Phase == "calibration" && o.Slot.CandidateId == id)
                .Select(o => o.Result).ToArray();
            double[] samples = rows.SelectMany(r => r.SamplesMilliseconds).ToArray();
            if (samples.Length == 0) return rows.FirstOrDefault() ?? Failure(byId[id], false, null, "No observations.");
            DistributionSummary stats = StableStatistics.Summarize(samples);
            CandidateResult first = rows[0];
            bool valid = rows.All(r => r.Compiled && r.Error is null && r.Correctness?.Passed == true);
            return first with { SamplesMilliseconds = samples, Timing = stats,
                Stable = valid && stats.CoefficientOfVariation <= manifest.MaximumCoefficientOfVariation,
                Error = valid ? null : "One or more raw observations failed; see paired evidence." };
        }

        void Save(TuningRunReport? report)
        {
            if (checkpointOptions is not null)
                PairedCheckpointStore.Write(checkpointOptions.Path, new(PairedProtocol.CheckpointSchema,
                    PairedProtocol.Id, identity, session, observations, selected, selectionLock, report, historical));
        }
    }
}
