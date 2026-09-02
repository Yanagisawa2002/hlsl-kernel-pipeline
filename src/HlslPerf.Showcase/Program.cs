using System.Globalization;
using System.Text;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;

namespace HlslPerf.Showcase;

internal static class Program
{
    private static readonly string[] Levels = ["low", "medium", "high", "extreme"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            string repositoryRoot = FindRepositoryRoot();
            ShowcaseOptions options = ParseOptions(args);
            string outputRoot = options.OutputDirectory ?? Path.Combine(
                repositoryRoot,
                ".hlslperf",
                "showcase",
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(outputRoot);

            List<ShowcaseLevelSummary> summaries = [];
            using D3D12Tuner tuner = new();
            DeviceFingerprint device = tuner.DescribeDevice();
            Console.WriteLine($"Adapter: {device.AdapterName}");
            Console.WriteLine($"Driver:  {device.DriverVersion}");
            Console.WriteLine($"Output:  {outputRoot}");

            if (options.StressGrid)
            {
                StressGridArtifacts artifacts = StressGridRunner.Run(
                    repositoryRoot,
                    outputRoot,
                    tuner,
                    device,
                    options.BudgetMilliseconds);
                Console.WriteLine();
                Console.WriteLine($"Stress grid:     {artifacts.CsvPath}");
                Console.WriteLine($"Heatmap:         {artifacts.SvgPath}");
                Console.WriteLine(
                    $"Budget:          {artifacts.EffectiveBudgetMilliseconds:0.0000} ms " +
                    $"({(artifacts.EffectiveBudgetMilliseconds == artifacts.RequestedBudgetMilliseconds ? "requested" : "measured fit")})");
                Console.WriteLine($"Budget crossing: {artifacts.BudgetCrossingGifPath ?? "not found"}");
                return artifacts.BudgetCrossingGifPath is null ? 2 : 0;
            }

            foreach (string level in Levels)
                summaries.Add(RunLevel(repositoryRoot, outputRoot, level, tuner));

            ShowcaseLevelSummary best = summaries
                .Where(summary => !summary.RetainedBaseline)
                .OrderByDescending(summary => summary.Speedup)
                .FirstOrDefault() ?? summaries.OrderByDescending(summary => summary.Speedup).First();
            WriteSummary(outputRoot, device, summaries, best);
            Console.WriteLine();
            Console.WriteLine($"Best pressure level: {best.Level} ({best.Speedup:0.0000}× baseline)");
            Console.WriteLine($"Summary: {Path.Combine(outputRoot, "showcase-summary.json")}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            if (Environment.GetEnvironmentVariable("HLSLPERF_TRACE") == "1")
                Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static ShowcaseLevelSummary RunLevel(
        string repositoryRoot,
        string outputRoot,
        string level,
        D3D12Tuner tuner)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {level.ToUpperInvariant()} PRESSURE ===");
        string manifestPath = Path.Combine(repositoryRoot, "showcase", "manifests", $"scan-particles-{level}.json");
        TuningManifest manifest = TuningManifest.Load(manifestPath);
        ScanParticleWorkload workload = new();
        string levelDirectory = Path.Combine(outputRoot, level);
        string captureDirectory = Path.Combine(levelDirectory, "gpu-captures");
        Directory.CreateDirectory(captureDirectory);
        Dictionary<string, string> capturePaths = new(StringComparer.Ordinal);

        TuningRunReport? report = null;
        SelectionResult? selection = null;
        CandidateResult? baseline = null;
        CandidateResult? selected = null;
        const int maximumAttempts = 3;
        for (int attempt = 1; attempt <= maximumAttempts; ++attempt)
        {
            capturePaths.Clear();
            report = tuner.Run(
                manifest,
                manifestPath,
                workload,
                progress => PrintProgress(progress),
                default,
                Path.Combine(repositoryRoot, ".hlslperf", "cache", "dxil"),
                (candidate, output) =>
                {
                    string path = Path.Combine(captureDirectory, candidate.Id + ".rgba");
                    File.WriteAllBytes(path, output.Span);
                    capturePaths[candidate.Id] = path;
                });
            selection = report.Selection
                ?? throw new InvalidDataException($"No deployable candidate was selected for {level} pressure.");
            baseline = report.Candidates.Single(candidate => candidate.CandidateId == report.BaselineCandidateId);
            selected = report.Candidates.Single(candidate => candidate.CandidateId == selection.CandidateId);
            if (baseline.Correctness?.Passed != true || selected.Correctness?.Passed != true ||
                baseline.Timing is null || selected.Timing is null)
                throw new InvalidDataException(
                    $"The {level} baseline or selected candidate failed correctness or timing.");
            if (baseline.Stable && selected.Stable)
                break;
            if (attempt == maximumAttempts)
                throw new InvalidDataException(
                    $"The {level} baseline or selected candidate remained noisy after {maximumAttempts} attempts.");
            File.WriteAllText(
                Path.Combine(levelDirectory, $"run-rejected-attempt-{attempt}.json"),
                JsonSerializer.Serialize(report, JsonDefaults.Options),
                new UTF8Encoding(false));
            Console.WriteLine(
                $"{level}: retrying noisy {(!baseline.Stable ? "baseline" : "selected")} " +
                $"({attempt}/{maximumAttempts})");
        }
        TuningRunReport finalReport = report!;
        SelectionResult finalSelection = selection!;
        CandidateResult finalBaseline = baseline!;
        CandidateResult finalSelected = selected!;
        ReportArtifacts reportArtifacts = ReportWriter.Write(finalReport, levelDirectory);
        if (!capturePaths.TryGetValue(finalBaseline.CandidateId, out string? baselineCapture) ||
            !capturePaths.TryGetValue(finalSelected.CandidateId, out string? selectedCapture))
            throw new InvalidDataException($"The {level} baseline or selected GPU frame atlas was not captured.");

        byte[] baselineAtlas = File.ReadAllBytes(baselineCapture);
        byte[] selectedAtlas = File.ReadAllBytes(selectedCapture);
        VisualArtifacts visual = VisualComposer.Write(
            level,
            finalReport.Device,
            workload.ElementCount,
            workload.ScanRepeats,
            workload.Width,
            workload.Height,
            workload.FrameCount,
            finalBaseline,
            finalSelected,
            baselineAtlas,
            selectedAtlas,
            Path.Combine(levelDirectory, "visual"));
        RetainSelectedCaptures(captureDirectory, baselineCapture, selectedCapture);

        DistributionSummary baselineTiming = finalBaseline.Timing!;
        DistributionSummary selectedTiming = finalSelected.Timing!;
        double speedup = baselineTiming.MedianMilliseconds / selectedTiming.MedianMilliseconds;
        ShowcaseLevelSummary summary = new(
            level,
            workload.ElementCount,
            workload.ScanRepeats,
            finalReport.Candidates.Count,
            finalReport.Candidates.Count(candidate => candidate.Correctness?.Passed == true),
            finalReport.Candidates.Count(candidate => candidate.Stable),
            finalBaseline.CandidateId,
            finalSelected.CandidateId,
            finalSelection.RetainedBaseline,
            finalBaseline.Stable,
            finalSelected.Stable,
            baselineTiming.MedianMilliseconds,
            baselineTiming.P95Milliseconds,
            baselineTiming.CoefficientOfVariation,
            selectedTiming.MedianMilliseconds,
            selectedTiming.P95Milliseconds,
            selectedTiming.CoefficientOfVariation,
            speedup,
            finalSelected.Correctness!.ActualSha256,
            reportArtifacts.RunJsonPath,
            visual.GifPath,
            visual.Mp4Path);
        File.WriteAllText(
            Path.Combine(levelDirectory, "level-summary.json"),
            JsonSerializer.Serialize(summary, JsonDefaults.Options));
        Console.WriteLine(
            $"{level}: {ShortCandidate(finalSelected)} · {selectedTiming.MedianMilliseconds:0.0000} ms · " +
            $"{speedup:0.0000}× · GPU output identical");
        return summary;
    }

    private static void PrintProgress(TuningProgress progress)
    {
        if (progress.Stage == "compile")
        {
            Console.Write($"[{progress.CandidateIndex,2}/{progress.CandidateCount}] {progress.CandidateId} ... ");
            return;
        }
        if (progress.Stage == "measure")
            return;
        CandidateResult result = progress.Result!;
        if (result.Timing is null)
        {
            Console.WriteLine($"FAILED ({result.Error})");
            return;
        }
        Console.WriteLine(
            $"{result.Timing.MedianMilliseconds:0.0000} ms · p95 {result.Timing.P95Milliseconds:0.0000} · " +
            $"CV {result.Timing.CoefficientOfVariation:P2} · " +
            $"{(result.Correctness?.Passed == true ? "correct" : "INCORRECT")} · " +
            $"{(result.Stable ? "stable" : "noisy")}");
    }

    private static void RetainSelectedCaptures(string captureDirectory, params string[] retainedPaths)
    {
        string root = Path.GetFullPath(captureDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        HashSet<string> retained = retainedPaths.Select(path => Path.GetFullPath(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(captureDirectory, "*.rgba", SearchOption.TopDirectoryOnly))
        {
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Capture cleanup escaped its output directory: {fullPath}");
            if (!retained.Contains(fullPath))
                File.Delete(fullPath);
        }
    }

    private static void WriteSummary(
        string outputRoot,
        DeviceFingerprint device,
        IReadOnlyList<ShowcaseLevelSummary> summaries,
        ShowcaseLevelSummary best)
    {
        File.WriteAllText(
            Path.Combine(outputRoot, "showcase-summary.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                createdUtc = DateTimeOffset.UtcNow,
                device,
                capturePolicy = "sequential baseline/tuned; same deterministic GPU frame atlas; byte equality required",
                bestLevel = best.Level,
                levels = summaries
            }, JsonDefaults.Options));

        StringBuilder csv = new();
        csv.AppendLine("level,element_count,scan_repeats,correct,stable,baseline_stable,selected_stable,baseline,selected,baseline_median_ms,baseline_cv,selected_median_ms,selected_p95_ms,selected_cv,speedup,retained_baseline");
        foreach (ShowcaseLevelSummary summary in summaries)
        {
            csv.AppendLine(string.Join(',',
                summary.Level,
                summary.ElementCount.ToString(CultureInfo.InvariantCulture),
                summary.ScanRepeats.ToString(CultureInfo.InvariantCulture),
                summary.CorrectCount.ToString(CultureInfo.InvariantCulture),
                summary.StableCount.ToString(CultureInfo.InvariantCulture),
                summary.BaselineStable.ToString(CultureInfo.InvariantCulture),
                summary.SelectedStable.ToString(CultureInfo.InvariantCulture),
                Csv(summary.BaselineCandidateId),
                Csv(summary.SelectedCandidateId),
                summary.BaselineMedianMilliseconds.ToString("R", CultureInfo.InvariantCulture),
                summary.BaselineCoefficientOfVariation.ToString("R", CultureInfo.InvariantCulture),
                summary.SelectedMedianMilliseconds.ToString("R", CultureInfo.InvariantCulture),
                summary.SelectedP95Milliseconds.ToString("R", CultureInfo.InvariantCulture),
                summary.SelectedCoefficientOfVariation.ToString("R", CultureInfo.InvariantCulture),
                summary.Speedup.ToString("R", CultureInfo.InvariantCulture),
                summary.RetainedBaseline.ToString(CultureInfo.InvariantCulture)));
        }
        File.WriteAllText(Path.Combine(outputRoot, "showcase-summary.csv"), csv.ToString());
    }

    private static string ShortCandidate(CandidateResult candidate)
    {
        int elementsPerThread = candidate.Defines["HLSLPERF_ELEMENTS_PER_THREAD"];
        bool singlePass = candidate.Defines.TryGetValue("HLSLPERF_SCAN_BACKEND", out int backend) && backend == 3;
        int itemsPerThread = singlePass &&
            candidate.Defines.TryGetValue("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", out int scale)
                ? checked(elementsPerThread * scale)
                : elementsPerThread;
        return $"{Backend(candidate)} · group={candidate.Defines["HLSLPERF_GROUP_SIZE"]}, " +
            $"{(singlePass ? "IPT" : "EPT")}={itemsPerThread}";
    }

    private static string Backend(CandidateResult candidate) =>
        candidate.Defines.TryGetValue("HLSLPERF_SCAN_BACKEND", out int backend)
            ? backend switch
            {
                2 => "wave",
                3 => "single-pass",
                _ => "blelloch"
            }
            : "blelloch";

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static ShowcaseOptions ParseOptions(string[] args)
    {
        string? output = null;
        bool stressGrid = false;
        double budgetMilliseconds = 1000.0 / 120.0;
        for (int index = 0; index < args.Length; ++index)
        {
            switch (args[index])
            {
                case "--output":
                    if (++index >= args.Length)
                        throw new ArgumentException("--output requires a directory.");
                    output = Path.GetFullPath(args[index]);
                    break;
                case "--stress-grid":
                    stressGrid = true;
                    break;
                case "--budget-ms":
                    if (++index >= args.Length ||
                        !double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out budgetMilliseconds) ||
                        !double.IsFinite(budgetMilliseconds) || budgetMilliseconds <= 0)
                        throw new ArgumentException("--budget-ms requires a positive finite number.");
                    break;
                default:
                    throw new ArgumentException(
                        "Usage: hlslperf-showcase [--output <directory>] [--stress-grid] [--budget-ms <milliseconds>]");
            }
        }
        return new ShowcaseOptions(output, stressGrid, budgetMilliseconds);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HlslKernelPipeline.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run the showcase from inside the HlslKernelPipeline repository.");
    }
}

internal sealed record ShowcaseOptions(string? OutputDirectory, bool StressGrid, double BudgetMilliseconds);

internal sealed record ShowcaseLevelSummary(
    string Level,
    int ElementCount,
    int ScanRepeats,
    int CandidateCount,
    int CorrectCount,
    int StableCount,
    string BaselineCandidateId,
    string SelectedCandidateId,
    bool RetainedBaseline,
    bool BaselineStable,
    bool SelectedStable,
    double BaselineMedianMilliseconds,
    double BaselineP95Milliseconds,
    double BaselineCoefficientOfVariation,
    double SelectedMedianMilliseconds,
    double SelectedP95Milliseconds,
    double SelectedCoefficientOfVariation,
    double Speedup,
    string OutputSha256,
    string RunJsonPath,
    string GifPath,
    string Mp4Path);
