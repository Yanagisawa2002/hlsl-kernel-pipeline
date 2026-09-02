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
            string outputRoot = ParseOutput(args) ?? Path.Combine(
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

        TuningRunReport report = tuner.Run(
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
        ReportArtifacts reportArtifacts = ReportWriter.Write(report, levelDirectory);
        SelectionResult selection = report.Selection
            ?? throw new InvalidDataException($"No deployable candidate was selected for {level} pressure.");
        CandidateResult baseline = report.Candidates.Single(candidate => candidate.CandidateId == report.BaselineCandidateId);
        CandidateResult selected = report.Candidates.Single(candidate => candidate.CandidateId == selection.CandidateId);
        if (baseline.Correctness?.Passed != true || selected.Correctness?.Passed != true)
            throw new InvalidDataException($"The {level} baseline or selected candidate failed the GPU output oracle.");
        if (!capturePaths.TryGetValue(baseline.CandidateId, out string? baselineCapture) ||
            !capturePaths.TryGetValue(selected.CandidateId, out string? selectedCapture))
            throw new InvalidDataException($"The {level} baseline or selected GPU frame atlas was not captured.");

        byte[] baselineAtlas = File.ReadAllBytes(baselineCapture);
        byte[] selectedAtlas = File.ReadAllBytes(selectedCapture);
        VisualArtifacts visual = VisualComposer.Write(
            level,
            report.Device,
            workload.ElementCount,
            workload.ScanRepeats,
            workload.Width,
            workload.Height,
            workload.FrameCount,
            baseline,
            selected,
            baselineAtlas,
            selectedAtlas,
            Path.Combine(levelDirectory, "visual"));
        RetainSelectedCaptures(captureDirectory, baselineCapture, selectedCapture);

        DistributionSummary baselineTiming = baseline.Timing!;
        DistributionSummary selectedTiming = selected.Timing!;
        double speedup = baselineTiming.MedianMilliseconds / selectedTiming.MedianMilliseconds;
        ShowcaseLevelSummary summary = new(
            level,
            workload.ElementCount,
            workload.ScanRepeats,
            report.Candidates.Count,
            report.Candidates.Count(candidate => candidate.Correctness?.Passed == true),
            report.Candidates.Count(candidate => candidate.Stable),
            baseline.CandidateId,
            selected.CandidateId,
            selection.RetainedBaseline,
            baselineTiming.MedianMilliseconds,
            baselineTiming.P95Milliseconds,
            selectedTiming.MedianMilliseconds,
            selectedTiming.P95Milliseconds,
            selectedTiming.CoefficientOfVariation,
            speedup,
            selected.Correctness!.ActualSha256,
            reportArtifacts.RunJsonPath,
            visual.GifPath,
            visual.Mp4Path);
        File.WriteAllText(
            Path.Combine(levelDirectory, "level-summary.json"),
            JsonSerializer.Serialize(summary, JsonDefaults.Options));
        Console.WriteLine(
            $"{level}: {ShortCandidate(selected)} · {selectedTiming.MedianMilliseconds:0.0000} ms · " +
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
        csv.AppendLine("level,element_count,scan_repeats,correct,stable,baseline,selected,baseline_median_ms,selected_median_ms,selected_p95_ms,selected_cv,speedup,retained_baseline");
        foreach (ShowcaseLevelSummary summary in summaries)
        {
            csv.AppendLine(string.Join(',',
                summary.Level,
                summary.ElementCount.ToString(CultureInfo.InvariantCulture),
                summary.ScanRepeats.ToString(CultureInfo.InvariantCulture),
                summary.CorrectCount.ToString(CultureInfo.InvariantCulture),
                summary.StableCount.ToString(CultureInfo.InvariantCulture),
                Csv(summary.BaselineCandidateId),
                Csv(summary.SelectedCandidateId),
                summary.BaselineMedianMilliseconds.ToString("R", CultureInfo.InvariantCulture),
                summary.SelectedMedianMilliseconds.ToString("R", CultureInfo.InvariantCulture),
                summary.SelectedP95Milliseconds.ToString("R", CultureInfo.InvariantCulture),
                summary.SelectedCoefficientOfVariation.ToString("R", CultureInfo.InvariantCulture),
                summary.Speedup.ToString("R", CultureInfo.InvariantCulture),
                summary.RetainedBaseline.ToString(CultureInfo.InvariantCulture)));
        }
        File.WriteAllText(Path.Combine(outputRoot, "showcase-summary.csv"), csv.ToString());
    }

    private static string ShortCandidate(CandidateResult candidate) =>
        $"{Backend(candidate)} · group={candidate.Defines["HLSLPERF_GROUP_SIZE"]}, " +
        $"EPT={candidate.Defines["HLSLPERF_ELEMENTS_PER_THREAD"]}";

    private static string Backend(CandidateResult candidate) =>
        candidate.Defines.TryGetValue("HLSLPERF_SCAN_BACKEND", out int backend) && backend == 2
            ? "wave"
            : "blelloch";

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static string? ParseOutput(string[] args)
    {
        if (args.Length == 0)
            return null;
        if (args.Length == 2 && args[0] == "--output")
            return Path.GetFullPath(args[1]);
        throw new ArgumentException("Usage: hlslperf-showcase [--output <directory>]");
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
    double BaselineMedianMilliseconds,
    double BaselineP95Milliseconds,
    double SelectedMedianMilliseconds,
    double SelectedP95Milliseconds,
    double SelectedCoefficientOfVariation,
    double Speedup,
    string OutputSha256,
    string RunJsonPath,
    string GifPath,
    string Mp4Path);
