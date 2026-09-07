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
            protocol = PairedProtocol.Id, manifestHash, effectiveManifest = manifest, DynamicExecutorSha256,
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
        if (deployable && PairedProfile.Create(report) is null)
        {
            rejections.Add("Deployment evidence content/identity validation rejected the profile.");
            evidence = evidence with { Deployable = false, Rejections = rejections };
            report = report with { PairedEvidence = evidence, Selection = report.Selection! with {
                UsedStablePool = false, Reason = "No deployable profile: " + string.Join(" ", rejections) } };
        }
        Save(report);
        return report;

        void Sample(IReadOnlyList<PairedSlot> slots)
        {
            foreach (var block in slots.GroupBy(s => (s.Block, s.ChallengerId)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dictionary<bool, ScenarioSession> arms = [];
                Dictionary<bool, string> preparationErrors = [];
                Dictionary<bool, long> itemCounts = [];
                try
                {
                    // Retain separate A and B resident rings simultaneously, even for baseline self-control.
                    foreach (bool isBaseline in new[] { true, false })
                    {
                        PairedSlot first = block.First(s => s.IsBaseline == isBaseline);
                        try
                        {
                            WorkloadScenario scenario = new($"paired-{first.Phase}-block-{first.Block}",
                                Enumerable.Range(first.InputSeed, manifest.PairedMeasurement.ResidentSlots).ToArray(),
                                MaximumAllocationBytes: manifest.PairedMeasurement.MaximumAllocationBytesPerArm);
                            ScenarioSession arm = PrepareScenario(manifest, fullPath, workload, byId[first.CandidateId], scenario, compilerCacheDirectory);
                            arms.Add(isBaseline, arm);
                            itemCounts.Add(isBaseline, workload.Build(scenario.Apply(manifest, 0), byId[first.CandidateId]).LogicalItemCount);
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { preparationErrors[isBaseline] = e.ToString(); }
                    }
                    foreach (PairedSlot slot in block)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DateTimeOffset sampleStart = DateTimeOffset.UtcNow;
                        KernelCandidate candidate = byId[slot.CandidateId];
                        CandidateResult result;
                        string? inputHash = null;
                        ScenarioSessionEvidence? scenarioEvidence = null;
                        IReadOnlyList<ScenarioVerification>? verified = null;
                        List<double> raw = [];
                        try
                        {
                            if (preparationErrors.TryGetValue(slot.IsBaseline, out string? error)) throw new InvalidDataException(error);
                            ScenarioSession arm = arms[slot.IsBaseline];
                            scenarioEvidence = arm.Evidence;
                            inputHash = ContentHash.Sha256(string.Join("/", arm.Evidence.Slots.Select(s => s.InputSha256)));
                            double warmup = 0;
                            for (int repeat = 0; manifest.WarmupDispatches > 0 && (repeat == 0 || warmup < manifest.MinimumWarmupMilliseconds); repeat++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (repeat >= 4096) throw new InvalidOperationException("Bounded warmup failed to meet the declared duration.");
                                warmup += arm.MeasureBatch(manifest.WarmupDispatches);
                            }
                            double batch = arm.MeasureBatch(manifest.DispatchesPerBatch);
                            double milliseconds = batch / manifest.DispatchesPerBatch;
                            raw.Add(milliseconds);
                            verified = arm.VerifyAll(captureVerifiedOutput is null ? null :
                                (_, bytes) => captureVerifiedOutput(candidate, bytes));
                            bool correct = verified.All(v => v.Correctness.Passed);
                            string actual = ContentHash.Sha256(string.Join("/", verified.Select(v => v.Correctness.ActualSha256)));
                            string expectedHash = ContentHash.Sha256(string.Join("/", verified.Select(v => v.Correctness.ExpectedSha256)));
                            result = new(candidate.Id, candidate.Defines, true, null,
                                new(correct, actual, expectedHash, "Every resident slot passed poison/re-execution and its CPU oracle."),
                                raw, manifest.DispatchesPerBatch, StableStatistics.Summarize(raw),
                                itemCounts[slot.IsBaseline] / milliseconds / 1000, true,
                                !correct ? "Resident slot correctness failed." : batch < manifest.MinimumBatchMilliseconds
                                    ? "Fixed batch was shorter than the predeclared minimum; increase dispatches in a new declared run." : null);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            result = Failure(candidate, false, null, exception.ToString()) with { SamplesMilliseconds = raw };
                        }
                        observations.Add(new(slot, sampleStart, DateTimeOffset.UtcNow, inputHash, result, scenarioEvidence, verified));
                        Save(null);
                        progress?.Invoke(new(observations.Count, slots.Count, candidate.Id,
                            $"{slot.Phase}/block-{slot.Block}/position-{slot.Position}", result));
                    }
                }
                finally
                {
                    foreach (ScenarioSession arm in arms.Values) arm.Dispose();
                }
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
