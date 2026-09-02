using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;

namespace HlslPerf.Showcase;

internal sealed record StressGridArtifacts(
    string CsvPath,
    string JsonPath,
    string SvgPath,
    string? BudgetCrossingGifPath,
    string? BudgetCrossingMp4Path,
    double RequestedBudgetMilliseconds,
    double EffectiveBudgetMilliseconds,
    StressPointSummary? BudgetCrossing);

internal sealed record StressPointSummary(
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
    int SelectedBackend,
    int SelectedGroupSize,
    int SelectedElementsPerThread,
    string OutputSha256,
    string RunJsonPath);

internal static class StressGridRunner
{
    private static readonly int[] ElementCounts =
    [
        2_097_152,
        4_194_304,
        8_388_608,
        12_582_912,
        16_777_216,
        25_165_824
    ];

    private static readonly int[] CoarseScanRepeats = [1, 4, 8, 12, 16, 24];

    public static StressGridArtifacts Run(
        string repositoryRoot,
        string outputRoot,
        D3D12Tuner tuner,
        DeviceFingerprint device,
        double requestedBudgetMilliseconds)
    {
        string templatePath = Path.Combine(
            repositoryRoot,
            "showcase",
            "manifests",
            "scan-particles-extreme.json");
        TuningManifest template = TuningManifest.Load(templatePath);
        string kernelPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(templatePath)!,
            template.KernelPath));
        string pointsRoot = Path.Combine(outputRoot, "stress-points");
        Directory.CreateDirectory(pointsRoot);

        Dictionary<(int ElementCount, int ScanRepeats), StressPointRun> points = [];
        foreach (int elementCount in ElementCounts)
        {
            foreach (int scanRepeats in CoarseScanRepeats)
                RunIfMissing(elementCount, scanRepeats);
        }

        // Add every integer repeat count around the observed baseline budget crossing.
        // This preserves broad coverage while making the decision boundary dense.
        foreach (int elementCount in ElementCounts)
        {
            List<StressPointRun> row = points.Values
                .Where(point => point.Summary.ElementCount == elementCount)
                .OrderBy(point => point.Summary.ScanRepeats)
                .ToList();
            for (int index = 0; index + 1 < row.Count; ++index)
            {
                StressPointSummary left = row[index].Summary;
                StressPointSummary right = row[index + 1].Summary;
                if (!Crosses(left.BaselineMedianMilliseconds, right.BaselineMedianMilliseconds, requestedBudgetMilliseconds))
                    continue;
                for (int repeat = left.ScanRepeats + 1; repeat < right.ScanRepeats; ++repeat)
                    RunIfMissing(elementCount, repeat);
                break;
            }
        }

        List<StressPointRun> ordered = points.Values
            .OrderBy(point => point.Summary.ElementCount)
            .ThenBy(point => point.Summary.ScanRepeats)
            .ToList();
        (StressPointRun? Crossing, double EffectiveBudget) crossing =
            SelectBudgetCrossing(ordered, requestedBudgetMilliseconds);

        string csvPath = Path.Combine(outputRoot, "stress-grid.csv");
        string jsonPath = Path.Combine(outputRoot, "stress-grid.json");
        string svgPath = Path.Combine(outputRoot, "stress-grid.svg");
        WriteCsv(csvPath, ordered.Select(point => point.Summary));
        WriteJson(
            jsonPath,
            device,
            requestedBudgetMilliseconds,
            crossing.EffectiveBudget,
            ordered.Select(point => point.Summary),
            crossing.Crossing?.Summary);
        WriteSvg(
            svgPath,
            device,
            crossing.EffectiveBudget,
            ordered.Select(point => point.Summary),
            crossing.Crossing?.Summary);

        string? gifPath = null;
        string? mp4Path = null;
        if (crossing.Crossing is not null)
        {
            VisualArtifacts visual = CaptureBudgetCrossing(
                repositoryRoot,
                outputRoot,
                tuner,
                device,
                crossing.Crossing,
                crossing.EffectiveBudget);
            gifPath = visual.GifPath;
            mp4Path = visual.Mp4Path;
        }

        return new StressGridArtifacts(
            csvPath,
            jsonPath,
            svgPath,
            gifPath,
            mp4Path,
            requestedBudgetMilliseconds,
            crossing.EffectiveBudget,
            crossing.Crossing?.Summary);

        void RunIfMissing(int elementCount, int scanRepeats)
        {
            if (points.ContainsKey((elementCount, scanRepeats)))
                return;
            StressPointRun point = RunPoint(
                repositoryRoot,
                pointsRoot,
                tuner,
                template,
                kernelPath,
                elementCount,
                scanRepeats);
            points.Add((elementCount, scanRepeats), point);
        }
    }

    private static StressPointRun RunPoint(
        string repositoryRoot,
        string pointsRoot,
        D3D12Tuner tuner,
        TuningManifest template,
        string kernelPath,
        int elementCount,
        int scanRepeats)
    {
        string pointName = $"e{elementCount:D8}-r{scanRepeats:D2}";
        string pointDirectory = Path.Combine(pointsRoot, pointName);
        Directory.CreateDirectory(pointDirectory);
        string manifestPath = Path.Combine(pointDirectory, "manifest.json");
        TuningManifest manifest = CreateManifest(template, kernelPath, elementCount, scanRepeats, pointName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonDefaults.Options),
            new UTF8Encoding(false));

        ScanParticleWorkload workload = new();
        TuningRunReport report = tuner.Run(
            manifest,
            manifestPath,
            workload,
            null,
            default,
            Path.Combine(repositoryRoot, ".hlslperf", "cache", "dxil"));
        ReportArtifacts artifacts = ReportWriter.Write(report, pointDirectory);
        SelectionResult selection = report.Selection
            ?? throw new InvalidDataException($"No deployable candidate was selected for stress point {pointName}.");
        CandidateResult baseline = report.Candidates.Single(candidate => candidate.CandidateId == report.BaselineCandidateId);
        CandidateResult selected = report.Candidates.Single(candidate => candidate.CandidateId == selection.CandidateId);
        if (baseline.Timing is null || selected.Timing is null ||
            baseline.Correctness?.Passed != true || selected.Correctness?.Passed != true)
            throw new InvalidDataException($"Stress point {pointName} did not produce correct timed baseline and selected candidates.");

        StressPointSummary summary = new(
            elementCount,
            scanRepeats,
            report.Candidates.Count,
            report.Candidates.Count(candidate => candidate.Correctness?.Passed == true),
            report.Candidates.Count(candidate => candidate.Stable),
            baseline.CandidateId,
            selected.CandidateId,
            selection.RetainedBaseline,
            baseline.Timing.MedianMilliseconds,
            baseline.Timing.P95Milliseconds,
            selected.Timing.MedianMilliseconds,
            selected.Timing.P95Milliseconds,
            selected.Timing.CoefficientOfVariation,
            baseline.Timing.MedianMilliseconds / selected.Timing.MedianMilliseconds,
            Define(selected, "HLSLPERF_SCAN_BACKEND"),
            Define(selected, "HLSLPERF_GROUP_SIZE"),
            Define(selected, "HLSLPERF_ELEMENTS_PER_THREAD"),
            selected.Correctness.ActualSha256,
            artifacts.RunJsonPath);
        File.WriteAllText(
            Path.Combine(pointDirectory, "point-summary.json"),
            JsonSerializer.Serialize(summary, JsonDefaults.Options),
            new UTF8Encoding(false));
        Console.WriteLine(
            $"grid {elementCount,10:N0} × {scanRepeats,2}: " +
            $"{summary.BaselineMedianMilliseconds,8:0.0000} → {summary.SelectedMedianMilliseconds,8:0.0000} ms · " +
            $"{summary.Speedup:0.0000}× · {BackendName(summary.SelectedBackend)} " +
            $"g{summary.SelectedGroupSize}/e{summary.SelectedElementsPerThread}");
        return new StressPointRun(manifest, manifestPath, report, summary);
    }

    private static TuningManifest CreateManifest(
        TuningManifest template,
        string kernelPath,
        int elementCount,
        int scanRepeats,
        string pointName)
    {
        Dictionary<string, long> parameters = new(template.Workload!.Parameters)
        {
            ["elementCount"] = elementCount,
            ["scanRepeats"] = scanRepeats
        };
        return new TuningManifest
        {
            SchemaVersion = template.SchemaVersion,
            Name = $"scan-particle-stress-{pointName}",
            KernelPath = kernelPath,
            KernelAbiVersion = template.KernelAbiVersion,
            Workload = new WorkloadSpec
            {
                Id = template.Workload.Id,
                Parameters = new ReadOnlyDictionary<string, long>(parameters)
            },
            EntryPoint = template.EntryPoint,
            ShaderModel = template.ShaderModel,
            WorkItemCount = elementCount,
            WarmupDispatches = template.WarmupDispatches,
            MinimumWarmupMilliseconds = template.MinimumWarmupMilliseconds,
            MeasurementBatches = template.MeasurementBatches,
            DispatchesPerBatch = template.DispatchesPerBatch,
            MinimumBatchMilliseconds = template.MinimumBatchMilliseconds,
            MaximumDispatchesPerBatch = template.MaximumDispatchesPerBatch,
            MaximumCoefficientOfVariation = template.MaximumCoefficientOfVariation,
            MinimumRequiredSpeedup = template.MinimumRequiredSpeedup,
            ThreadsPerGroupParameter = template.ThreadsPerGroupParameter,
            ElementsPerThreadParameter = template.ElementsPerThreadParameter,
            FixedDefines = template.FixedDefines,
            BaselineDefines = template.BaselineDefines,
            Axes = template.Axes,
            Correctness = template.Correctness
        };
    }

    private static (StressPointRun? Crossing, double EffectiveBudget) SelectBudgetCrossing(
        IReadOnlyList<StressPointRun> points,
        double requestedBudgetMilliseconds)
    {
        StressPointRun? crossing = points
            .Where(point => IsCrossing(point.Summary, requestedBudgetMilliseconds))
            .OrderByDescending(point => point.Summary.Speedup)
            .ThenByDescending(point => point.Summary.BaselineMedianMilliseconds - requestedBudgetMilliseconds)
            .FirstOrDefault();
        if (crossing is not null)
            return (crossing, requestedBudgetMilliseconds);

        // Hardware can move the entire sampled grid to one side of a conventional
        // frame budget. Fall back to a measured, explicitly reported budget that
        // lies between tuned p95 and baseline median; no duration is fabricated.
        crossing = points
            .Where(point => !point.Summary.RetainedBaseline &&
                point.Summary.SelectedP95Milliseconds < point.Summary.BaselineMedianMilliseconds)
            .OrderByDescending(point =>
                point.Summary.BaselineMedianMilliseconds / point.Summary.SelectedP95Milliseconds)
            .FirstOrDefault();
        if (crossing is null)
            return (null, requestedBudgetMilliseconds);
        double fittedBudget =
            (crossing.Summary.SelectedP95Milliseconds + crossing.Summary.BaselineMedianMilliseconds) / 2;
        return (crossing, fittedBudget);
    }

    private static bool IsCrossing(StressPointSummary point, double budgetMilliseconds) =>
        !point.RetainedBaseline &&
        point.BaselineMedianMilliseconds > budgetMilliseconds &&
        point.SelectedP95Milliseconds <= budgetMilliseconds;

    private static bool Crosses(double left, double right, double threshold) =>
        (left <= threshold && right > threshold) || (right <= threshold && left > threshold);

    private static VisualArtifacts CaptureBudgetCrossing(
        string repositoryRoot,
        string outputRoot,
        D3D12Tuner tuner,
        DeviceFingerprint device,
        StressPointRun point,
        double budgetMilliseconds)
    {
        string crossingDirectory = Path.Combine(outputRoot, "budget-crossing");
        string captureDirectory = Path.Combine(crossingDirectory, "gpu-captures");
        Directory.CreateDirectory(captureDirectory);
        Dictionary<string, string> captures = new(StringComparer.Ordinal);
        ScanParticleWorkload workload = new();
        Console.WriteLine();
        Console.WriteLine(
            $"Capturing budget crossing at {point.Summary.ElementCount:N0} elements × " +
            $"{point.Summary.ScanRepeats} scans ({budgetMilliseconds:0.0000} ms budget)...");
        TuningRunReport captureReport = tuner.Run(
            point.Manifest,
            point.ManifestPath,
            workload,
            null,
            default,
            Path.Combine(repositoryRoot, ".hlslperf", "cache", "dxil"),
            (candidate, output) =>
            {
                string path = Path.Combine(captureDirectory, candidate.Id + ".rgba");
                File.WriteAllBytes(path, output.Span);
                captures[candidate.Id] = path;
            });
        ReportWriter.Write(captureReport, Path.Combine(crossingDirectory, "capture-run"));

        CandidateResult baseline = point.Report.Candidates.Single(
            candidate => candidate.CandidateId == point.Summary.BaselineCandidateId);
        CandidateResult selected = point.Report.Candidates.Single(
            candidate => candidate.CandidateId == point.Summary.SelectedCandidateId);
        if (!captures.TryGetValue(baseline.CandidateId, out string? baselinePath) ||
            !captures.TryGetValue(selected.CandidateId, out string? selectedPath))
            throw new InvalidDataException("The budget-crossing baseline or selected GPU atlas was not captured.");
        byte[] baselineAtlas = File.ReadAllBytes(baselinePath);
        byte[] selectedAtlas = File.ReadAllBytes(selectedPath);
        VisualArtifacts visual = VisualComposer.WriteBudgetCrossing(
            device,
            workload.ElementCount,
            workload.ScanRepeats,
            workload.Width,
            workload.Height,
            workload.FrameCount,
            budgetMilliseconds,
            baseline,
            selected,
            baselineAtlas,
            selectedAtlas,
            Path.Combine(crossingDirectory, "visual"));
        RetainCaptures(captureDirectory, baselinePath, selectedPath);
        return visual;
    }

    private static void RetainCaptures(string captureDirectory, params string[] retainedPaths)
    {
        string root = Path.GetFullPath(captureDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        HashSet<string> retained = retainedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(captureDirectory, "*.rgba", SearchOption.TopDirectoryOnly))
        {
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Capture cleanup escaped its output directory: {fullPath}");
            if (!retained.Contains(fullPath))
                File.Delete(fullPath);
        }
    }

    private static void WriteCsv(string path, IEnumerable<StressPointSummary> points)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "element_count,scan_repeats,candidates,correct,stable,baseline_median_ms,baseline_p95_ms," +
            "selected_median_ms,selected_p95_ms,selected_cv,speedup,retained_baseline,backend,group_size," +
            "elements_per_thread,baseline_candidate,selected_candidate,output_sha256,run_json");
        foreach (StressPointSummary point in points)
        {
            csv.AppendLine(string.Join(',',
                Invariant(point.ElementCount),
                Invariant(point.ScanRepeats),
                Invariant(point.CandidateCount),
                Invariant(point.CorrectCount),
                Invariant(point.StableCount),
                Invariant(point.BaselineMedianMilliseconds),
                Invariant(point.BaselineP95Milliseconds),
                Invariant(point.SelectedMedianMilliseconds),
                Invariant(point.SelectedP95Milliseconds),
                Invariant(point.SelectedCoefficientOfVariation),
                Invariant(point.Speedup),
                point.RetainedBaseline.ToString(CultureInfo.InvariantCulture),
                Csv(BackendName(point.SelectedBackend)),
                Invariant(point.SelectedGroupSize),
                Invariant(point.SelectedElementsPerThread),
                Csv(point.BaselineCandidateId),
                Csv(point.SelectedCandidateId),
                Csv(point.OutputSha256),
                Csv(point.RunJsonPath)));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteJson(
        string path,
        DeviceFingerprint device,
        double requestedBudgetMilliseconds,
        double effectiveBudgetMilliseconds,
        IEnumerable<StressPointSummary> points,
        StressPointSummary? crossing)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                createdUtc = DateTimeOffset.UtcNow,
                device,
                requestedBudgetMilliseconds,
                effectiveBudgetMilliseconds,
                budgetMode = requestedBudgetMilliseconds == effectiveBudgetMilliseconds
                    ? "requested"
                    : "measured-fit-between-selected-p95-and-baseline-median",
                capturePolicy = "same deterministic GPU atlas; byte equality required; cadence replay uses recorded per-plan GPU samples",
                budgetCrossing = crossing,
                points
            }, JsonDefaults.Options),
            new UTF8Encoding(false));
    }

    private static void WriteSvg(
        string path,
        DeviceFingerprint device,
        double budgetMilliseconds,
        IEnumerable<StressPointSummary> source,
        StressPointSummary? crossing)
    {
        List<StressPointSummary> points = source.ToList();
        int[] repeats = points.Select(point => point.ScanRepeats).Distinct().Order().ToArray();
        int[] elements = points.Select(point => point.ElementCount).Distinct().Order().ToArray();
        const int left = 190;
        const int top = 148;
        const int cellWidth = 52;
        const int cellHeight = 64;
        int width = left + repeats.Length * cellWidth + 38;
        int height = top + elements.Length * cellHeight + 92;
        StringBuilder svg = new();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        svg.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#071226\"/>");
        svg.AppendLine("<style>text{font-family:'Segoe UI',sans-serif;fill:#eaf2ff}.muted{fill:#91a4bd}.mono{font-family:Consolas,monospace}</style>");
        svg.AppendLine("<text x=\"28\" y=\"38\" font-size=\"24\" font-weight=\"700\">HLSL scan stress grid · measured GPU speedup</text>");
        svg.AppendLine($"<text x=\"28\" y=\"66\" font-size=\"13\" class=\"muted\">{EscapeXml(device.AdapterName)} · budget {budgetMilliseconds:0.0000} ms · cell = baseline median / selected median</text>");
        svg.AppendLine("<text x=\"28\" y=\"94\" font-size=\"12\" fill=\"#3fe7a4\">green = tuned selected</text>");
        svg.AppendLine("<text x=\"205\" y=\"94\" font-size=\"12\" fill=\"#ffbe5c\">gold outline = budget crossing</text>");
        svg.AppendLine($"<text x=\"{left}\" y=\"122\" font-size=\"12\" class=\"muted\">scan repeats per plan →</text>");
        for (int column = 0; column < repeats.Length; ++column)
            svg.AppendLine($"<text x=\"{left + column * cellWidth + cellWidth / 2}\" y=\"140\" font-size=\"11\" text-anchor=\"middle\" class=\"mono\">{repeats[column]}</text>");

        for (int row = 0; row < elements.Length; ++row)
        {
            int elementCount = elements[row];
            int y = top + row * cellHeight;
            svg.AppendLine($"<text x=\"{left - 14}\" y=\"{y + 28}\" font-size=\"12\" text-anchor=\"end\" class=\"mono\">{elementCount / 1_048_576.0:0.#}M elements</text>");
            for (int column = 0; column < repeats.Length; ++column)
            {
                StressPointSummary? point = points.FirstOrDefault(candidate =>
                    candidate.ElementCount == elementCount && candidate.ScanRepeats == repeats[column]);
                int x = left + column * cellWidth;
                if (point is null)
                {
                    svg.AppendLine($"<rect x=\"{x + 2}\" y=\"{y + 2}\" width=\"{cellWidth - 4}\" height=\"{cellHeight - 4}\" rx=\"6\" fill=\"#101e34\"/>");
                    continue;
                }
                string fill = HeatColor(point.Speedup, point.RetainedBaseline);
                bool isCrossing = crossing is not null &&
                    crossing.ElementCount == point.ElementCount && crossing.ScanRepeats == point.ScanRepeats;
                string stroke = isCrossing ? "#ffbe5c" : "#263b58";
                int strokeWidth = isCrossing ? 3 : 1;
                svg.AppendLine($"<rect x=\"{x + 2}\" y=\"{y + 2}\" width=\"{cellWidth - 4}\" height=\"{cellHeight - 4}\" rx=\"6\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\"/>");
                svg.AppendLine($"<text x=\"{x + cellWidth / 2}\" y=\"{y + 25}\" font-size=\"11\" font-weight=\"700\" text-anchor=\"middle\" class=\"mono\">{point.Speedup:0.000}×</text>");
                svg.AppendLine($"<text x=\"{x + cellWidth / 2}\" y=\"{y + 43}\" font-size=\"9\" text-anchor=\"middle\" class=\"mono\">{point.SelectedMedianMilliseconds:0.00}ms</text>");
            }
        }
        svg.AppendLine($"<text x=\"28\" y=\"{height - 34}\" font-size=\"12\" class=\"muted\">All cells: same deterministic image workload; correctness gated by SHA-256; upload/readback excluded from GPU timing.</text>");
        svg.AppendLine("</svg>");
        File.WriteAllText(path, svg.ToString(), new UTF8Encoding(false));
    }

    private static string HeatColor(double speedup, bool retainedBaseline)
    {
        if (retainedBaseline)
            return "#243247";
        double amount = Math.Clamp((speedup - 1.0) / 0.16, 0, 1);
        int red = (int)Math.Round(27 - amount * 15);
        int green = (int)Math.Round(74 + amount * 91);
        int blue = (int)Math.Round(78 + amount * 31);
        return $"#{red:x2}{green:x2}{blue:x2}";
    }

    private static int Define(CandidateResult candidate, string name) =>
        candidate.Defines.TryGetValue(name, out int value)
            ? value
            : throw new InvalidDataException($"Candidate '{candidate.CandidateId}' is missing define '{name}'.");

    private static string BackendName(int backend) => backend switch
    {
        1 => "blelloch",
        2 => "wave",
        3 => "single-pass",
        _ => $"backend-{backend}"
    };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private sealed record StressPointRun(
        TuningManifest Manifest,
        string ManifestPath,
        TuningRunReport Report,
        StressPointSummary Summary);
}
