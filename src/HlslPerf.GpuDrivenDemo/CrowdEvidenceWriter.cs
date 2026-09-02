using System.Globalization;
using System.Text;
using HlslPerf.Core;

namespace HlslPerf.GpuDrivenDemo;

internal static class CrowdEvidenceWriter
{
    public static CrowdEvidenceArtifacts Write(
        string outputDirectory,
        DeviceFingerprint device,
        IReadOnlyList<CrowdLevelEvidence> levels)
    {
        if (levels.Count == 0)
            throw new ArgumentException("At least one pressure level is required.", nameof(levels));
        Directory.CreateDirectory(outputDirectory);
        string csvPath = Path.Combine(outputDirectory, "crowd-vfx-pressure-grid.csv");
        string svgPath = Path.Combine(outputDirectory, "crowd-vfx-pressure-grid.svg");
        WriteCsv(csvPath, levels);
        WriteSvg(svgPath, device, levels);
        return new CrowdEvidenceArtifacts(csvPath, svgPath);
    }

    private static void WriteCsv(string path, IReadOnlyList<CrowdLevelEvidence> levels)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "level,agents,frames,mean_visible,candidates,correct,stable,baseline,selected," +
            "baseline_median_ms,baseline_p95_ms,selected_median_ms,selected_p95_ms,speedup," +
            "retained_baseline,atlas_sha256");
        foreach (CrowdLevelEvidence item in levels)
        {
            csv.AppendLine(string.Join(',',
                item.Level,
                Invariant(item.AgentCount),
                Invariant(item.FrameCount),
                Invariant(item.MeanVisibleCount),
                Invariant(item.CandidateCount),
                Invariant(item.CorrectCandidateCount),
                Invariant(item.StableCandidateCount),
                Csv(item.BaselineCandidateId),
                Csv(item.SelectedCandidateId),
                Invariant(item.BaselineMedianMilliseconds),
                Invariant(item.BaselineP95Milliseconds),
                Invariant(item.SelectedMedianMilliseconds),
                Invariant(item.SelectedP95Milliseconds),
                Invariant(item.Speedup),
                item.RetainedBaseline.ToString(CultureInfo.InvariantCulture),
                item.AtlasSha256));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteSvg(
        string path,
        DeviceFingerprint device,
        IReadOnlyList<CrowdLevelEvidence> levels)
    {
        const int width = 1180;
        int height = 190 + levels.Count * 96;
        StringBuilder svg = new();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        svg.AppendLine("<defs><linearGradient id=\"bg\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop stop-color=\"#061020\"/><stop offset=\"1\" stop-color=\"#111f38\"/></linearGradient></defs>");
        svg.AppendLine("<style>text{font-family:'Segoe UI',sans-serif;fill:#edf6ff}.muted{fill:#91a4bb}.mono{font-family:Consolas,monospace}.title{font-weight:700}.good{fill:#50e3ae}</style>");
        svg.AppendLine($"<rect width=\"{width}\" height=\"{height}\" fill=\"url(#bg)\"/>");
        svg.AppendLine("<text x=\"38\" y=\"48\" font-size=\"27\" class=\"title\">GPU-DRIVEN CROWD/VFX · COMPLETE APPLICATION PLAN</text>");
        svg.AppendLine($"<text x=\"40\" y=\"78\" font-size=\"14\" class=\"muted\">{Escape(device.AdapterName)} · driver {Escape(device.DriverVersion)} · D3D12 / SM 6.6</text>");
        svg.AppendLine("<text x=\"40\" y=\"111\" font-size=\"13\" class=\"muted\">Same agents → visibility → compaction → tile histogram/offsets → bin scatter → tiled raster; final atlas SHA-256 gated.</text>");
        svg.AppendLine("<text x=\"38\" y=\"151\" font-size=\"12\" class=\"muted\">PRESSURE</text>");
        svg.AppendLine("<text x=\"174\" y=\"151\" font-size=\"12\" class=\"muted\">AGENTS / MEAN VISIBLE</text>");
        svg.AppendLine("<text x=\"430\" y=\"151\" font-size=\"12\" class=\"muted\">BASELINE MEDIAN / P95</text>");
        svg.AppendLine("<text x=\"690\" y=\"151\" font-size=\"12\" class=\"muted\">SELECTED MEDIAN / P95</text>");
        svg.AppendLine("<text x=\"965\" y=\"151\" font-size=\"12\" class=\"muted\">END-TO-END</text>");

        for (int index = 0; index < levels.Count; ++index)
        {
            CrowdLevelEvidence item = levels[index];
            int y = 168 + index * 96;
            string fill = HeatColor(item.Speedup, item.RetainedBaseline);
            svg.AppendLine($"<rect x=\"28\" y=\"{y}\" width=\"1124\" height=\"76\" rx=\"12\" fill=\"#101f37\" stroke=\"#29415e\"/>");
            svg.AppendLine($"<rect x=\"955\" y=\"{y + 10}\" width=\"181\" height=\"56\" rx=\"9\" fill=\"{fill}\"/>");
            svg.AppendLine($"<text x=\"46\" y=\"{y + 31}\" font-size=\"17\" class=\"title\">{Escape(item.Level.ToUpperInvariant())}</text>");
            svg.AppendLine($"<text x=\"46\" y=\"{y + 56}\" font-size=\"12\" class=\"muted\">{item.CorrectCandidateCount}/{item.CandidateCount} correct</text>");
            svg.AppendLine($"<text x=\"174\" y=\"{y + 33}\" font-size=\"17\" class=\"mono\">{item.AgentCount:N0}</text>");
            svg.AppendLine($"<text x=\"174\" y=\"{y + 57}\" font-size=\"12\" class=\"muted\">{item.MeanVisibleCount:N0} visible/frame</text>");
            svg.AppendLine($"<text x=\"430\" y=\"{y + 34}\" font-size=\"17\" class=\"mono\">{item.BaselineMedianMilliseconds:0.0000} / {item.BaselineP95Milliseconds:0.0000} ms</text>");
            svg.AppendLine($"<text x=\"690\" y=\"{y + 34}\" font-size=\"17\" class=\"mono\">{item.SelectedMedianMilliseconds:0.0000} / {item.SelectedP95Milliseconds:0.0000} ms</text>");
            svg.AppendLine($"<text x=\"690\" y=\"{y + 58}\" font-size=\"12\" class=\"muted\">{Escape(Program.ShortCandidate(item.SelectedDefines))}</text>");
            string result = item.RetainedBaseline ? "baseline kept" : $"{item.Speedup:0.000}×";
            svg.AppendLine($"<text x=\"1045\" y=\"{y + 45}\" text-anchor=\"middle\" font-size=\"22\" class=\"title\">{result}</text>");
        }

        svg.AppendLine($"<text x=\"38\" y=\"{height - 22}\" font-size=\"12\" class=\"muted\">GPU timestamps exclude upload/readback/media composition. Speedup is whole application-plan time, not isolated kernel time.</text>");
        svg.AppendLine("</svg>");
        File.WriteAllText(path, svg.ToString(), new UTF8Encoding(false));
    }

    private static string HeatColor(double speedup, bool retainedBaseline)
    {
        if (retainedBaseline)
            return "#27364c";
        double amount = Math.Clamp((speedup - 1) / 1.5, 0, 1);
        int red = (int)Math.Round(25 - amount * 12);
        int green = (int)Math.Round(91 + amount * 102);
        int blue = (int)Math.Round(102 + amount * 54);
        return $"#{red:x2}{green:x2}{blue:x2}";
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}

internal sealed record CrowdEvidenceArtifacts(string CsvPath, string SvgPath);
