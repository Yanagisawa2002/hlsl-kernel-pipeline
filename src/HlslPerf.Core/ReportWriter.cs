using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace HlslPerf.Core;

public sealed record ReportArtifacts(
    string RunJsonPath,
    string CandidatesCsvPath,
    string HtmlPath,
    string SvgPath,
    string? ProfileJsonPath);

public static class ReportWriter
{
    public static ReportArtifacts Write(TuningRunReport report, string outputDirectory)
    {
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        string runPath = Path.Combine(fullOutputDirectory, "run.json");
        string csvPath = Path.Combine(fullOutputDirectory, "candidates.csv");
        string htmlPath = Path.Combine(fullOutputDirectory, "report.html");
        string svgPath = Path.Combine(fullOutputDirectory, "comparison.svg");
        File.WriteAllText(runPath, JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
        File.WriteAllText(csvPath, BuildCsv(report), new UTF8Encoding(false));
        File.WriteAllText(htmlPath, BuildHtml(report), new UTF8Encoding(false));
        File.WriteAllText(svgPath, BuildSvg(report), new UTF8Encoding(false));

        string profileCandidatePath = Path.Combine(fullOutputDirectory, "profile.json");
        string? profilePath = null;
        TuningProfile? profile = CreateProfile(report);
        if (profile is not null)
        {
            profilePath = profileCandidatePath;
            File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, JsonDefaults.Options), new UTF8Encoding(false));
        }
        else if (File.Exists(profileCandidatePath))
        {
            File.Delete(profileCandidatePath);
        }

        return new ReportArtifacts(runPath, csvPath, htmlPath, svgPath, profilePath);
    }

    public static TuningProfile? CreateProfile(TuningRunReport report)
    {
        if (report.Selection is null || !report.Selection.UsedStablePool)
            return null;
        CandidateResult? selected = report.Candidates.FirstOrDefault(candidate =>
            candidate.CandidateId == report.Selection.CandidateId);
        if (selected?.Compiled != true || selected.Error is not null ||
            selected.Correctness?.Passed != true || !selected.Stable ||
            selected.Timing is null || selected.ThroughputMillionItemsPerSecond is null)
            return null;
        CandidateResult? baseline = report.Candidates.FirstOrDefault(candidate =>
            candidate.CandidateId == report.BaselineCandidateId);
        if (baseline?.Compiled != true || baseline.Error is not null ||
            baseline.Correctness?.Passed != true || !baseline.Stable || baseline.Timing is null)
            return null;
        double? speedup = baseline?.Timing is null
            ? null
            : baseline.Timing.MedianMilliseconds / selected.Timing.MedianMilliseconds;

        return new TuningProfile(
            "2.0",
            DateTimeOffset.UtcNow,
            ContentHash.CompatibilityKey(report.Device, report.ManifestSha256, report.KernelSha256),
            report.Device,
            report.ManifestSha256,
            report.KernelSha256,
            selected.CandidateId,
            selected.Defines,
            selected.Timing.MedianMilliseconds,
            selected.Timing.P95Milliseconds,
            selected.ThroughputMillionItemsPerSecond.Value,
            speedup,
            report.WorkloadId,
            report.KernelAbiVersion,
            selected.Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ProfileDefine(pair.Key, pair.Value))
                .ToArray());
    }

    private static string BuildCsv(TuningRunReport report)
    {
        StringBuilder csv = new();
        csv.AppendLine("candidate_id,compiled,correct,stable,rga_status,max_vgprs_used,max_live_vgprs,max_allocated_vgprs,max_sgprs_used,max_lds_bytes,max_scratch_bytes,min_occupancy_waves_per_simd,dispatches_per_batch,median_gpu_ms,p95_gpu_ms,cv,throughput_mitems_s,speedup_vs_baseline,defines,samples_gpu_ms,error");
        CandidateResult? baseline = report.Candidates.FirstOrDefault(candidate =>
            candidate.CandidateId == report.BaselineCandidateId);
        double? baselineMedian = baseline?.Timing?.MedianMilliseconds;
        foreach (CandidateResult candidate in report.Candidates)
        {
            double? speedup = baselineMedian.HasValue && candidate.Timing is not null
                ? baselineMedian.Value / candidate.Timing.MedianMilliseconds
                : null;
            string defines = string.Join(";", candidate.Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
            string samples = string.Join(";", candidate.SamplesMilliseconds.Select(Format));
            IReadOnlyList<RgaPassAnalysis> rga = candidate.StaticAnalysis?.Passes ?? [];
            csv.Append(Csv(candidate.CandidateId)).Append(',')
                .Append(candidate.Compiled ? "true" : "false").Append(',')
                .Append(candidate.Correctness?.Passed == true ? "true" : "false").Append(',')
                .Append(candidate.Stable ? "true" : "false").Append(',')
                .Append(Csv(candidate.StaticAnalysis?.Status ?? string.Empty)).Append(',')
                .Append(Max(rga, pass => pass.VgprsUsed)).Append(',')
                .Append(Max(rga, pass => pass.MaximumLiveVgprs)).Append(',')
                .Append(Max(rga, pass => pass.AllocatedVgprs)).Append(',')
                .Append(Max(rga, pass => pass.SgprsUsed)).Append(',')
                .Append(Max(rga, pass => pass.LdsBytes)).Append(',')
                .Append(Max(rga, pass => pass.ScratchBytes)).Append(',')
                .Append(Min(rga, pass => pass.OccupancyWavesPerSimd)).Append(',')
                .Append(candidate.MeasuredDispatchesPerBatch.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(candidate.Timing is null ? string.Empty : Format(candidate.Timing.MedianMilliseconds)).Append(',')
                .Append(candidate.Timing is null ? string.Empty : Format(candidate.Timing.P95Milliseconds)).Append(',')
                .Append(candidate.Timing is null ? string.Empty : Format(candidate.Timing.CoefficientOfVariation)).Append(',')
                .Append(candidate.ThroughputMillionItemsPerSecond is null ? string.Empty : Format(candidate.ThroughputMillionItemsPerSecond.Value)).Append(',')
                .Append(speedup is null ? string.Empty : Format(speedup.Value)).Append(',')
                .Append(Csv(defines)).Append(',')
                .Append(Csv(samples)).Append(',')
                .Append(Csv(candidate.Error ?? string.Empty))
                .AppendLine();
        }
        return csv.ToString();
    }

    private static string BuildHtml(TuningRunReport report)
    {
        CandidateResult? baseline = report.Candidates.FirstOrDefault(candidate =>
            candidate.CandidateId == report.BaselineCandidateId);
        CandidateResult? winner = report.Selection is null
            ? null
            : report.Candidates.FirstOrDefault(candidate => candidate.CandidateId == report.Selection.CandidateId);
        CandidateResult? observedFastest = report.Selection is null
            ? null
            : report.Candidates.FirstOrDefault(candidate =>
                candidate.CandidateId == report.Selection.ObservedFastestCandidateId);
        double? baselineMedian = baseline?.Timing?.MedianMilliseconds;
        double maximumSpeedup = report.Candidates
            .Where(candidate => candidate.Timing is not null && baselineMedian.HasValue)
            .Select(candidate => baselineMedian!.Value / candidate.Timing!.MedianMilliseconds)
            .DefaultIfEmpty(1)
            .Max();

        StringBuilder rows = new();
        foreach (CandidateResult candidate in report.Candidates
                     .OrderBy(candidate => candidate.Timing?.MedianMilliseconds ?? double.MaxValue))
        {
            double? speedup = candidate.Timing is not null && baselineMedian.HasValue
                ? baselineMedian.Value / candidate.Timing.MedianMilliseconds
                : null;
            double width = speedup is null ? 0 : Math.Max(1, speedup.Value / maximumSpeedup * 100);
            string classes = candidate.CandidateId == winner?.CandidateId
                ? "winner"
                : candidate.CandidateId == baseline?.CandidateId ? "baseline" : string.Empty;
            rows.Append("<tr class=\"").Append(classes).Append("\"><td>")
                .Append(Html(Friendly(candidate.Defines))).Append("</td><td>")
                .Append(Status(candidate)).Append("</td><td>")
                .Append(candidate.Timing is null ? "—" : Format(candidate.Timing.MedianMilliseconds)).Append("</td><td>")
                .Append(candidate.Timing is null ? "—" : Format(candidate.Timing.P95Milliseconds)).Append("</td><td>")
                .Append(candidate.Timing is null ? "—" : Format(candidate.Timing.CoefficientOfVariation * 100) + "%").Append("</td><td>")
                .Append(candidate.ThroughputMillionItemsPerSecond is null ? "—" : Format(candidate.ThroughputMillionItemsPerSecond.Value)).Append("</td><td>")
                .Append(speedup is null ? "—" : Format(speedup.Value) + "×").Append("</td><td class=\"bar-cell\"><span style=\"width:")
                .Append(width.ToString("0.##", CultureInfo.InvariantCulture)).Append("%\"></span></td><td>")
                .Append(Html(StaticEvidence(candidate))).Append("</td></tr>");
        }

        string selection = winner is null
            ? "No candidate passed the correctness and measurement gates."
            : report.Selection!.RetainedBaseline && observedFastest is not null
                ? $"Baseline retained · fastest observed {Html(Friendly(observedFastest.Defines))}"
                : $"{Html(Friendly(winner.Defines))} · {Format(winner.Timing!.MedianMilliseconds)} ms median · " +
                  $"{(baselineMedian.HasValue ? Format(baselineMedian.Value / winner.Timing.MedianMilliseconds) + "× baseline" : "baseline unavailable")}";
        string decision = Html(report.Selection?.Reason ?? "No deployment selection was possible.");
        return $$$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>HLSL kernel tuning · {{{Html(report.Device.AdapterName)}}}</title>
<style>
:root{color-scheme:dark;--bg:#0b1020;--panel:#121a2c;--line:#28334c;--text:#e9eefb;--muted:#99a6bf;--blue:#5aa9ff;--green:#4be3a4;--red:#ff6b7a}
*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 15% 0,#18294a 0,var(--bg) 42%);color:var(--text);font:14px/1.45 Inter,Segoe UI,sans-serif}
main{max-width:1240px;margin:auto;padding:36px 24px 64px}h1{font-size:30px;margin:0 0 4px}.sub{color:var(--muted);margin-bottom:26px}.cards{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin-bottom:22px}
.card,.panel{background:color-mix(in srgb,var(--panel) 93%,transparent);border:1px solid var(--line);border-radius:14px;box-shadow:0 14px 40px #0004}.card{padding:16px}.label{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.08em}.value{font-size:20px;font-weight:700;margin-top:5px}
.panel{padding:18px;overflow:auto}table{width:100%;border-collapse:collapse;min-width:920px}th,td{padding:10px 9px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}th{color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.06em}th:first-child,td:first-child{text-align:left}
tr.winner{background:#173d35}tr.baseline{background:#172d4b}.ok{color:var(--green)}.bad{color:var(--red)}.bar-cell{width:180px}.bar-cell span{display:block;height:9px;border-radius:9px;background:linear-gradient(90deg,var(--blue),var(--green));min-width:2px;transform-origin:left;animation:grow .8s cubic-bezier(.2,.8,.2,1) both}
@keyframes grow{from{transform:scaleX(0)}}html.capture .bar-cell span{animation:none;transform:scaleX(var(--capture-progress))}@media(prefers-reduced-motion:reduce){.bar-cell span{animation:none}}
.decision{margin:0 0 22px;padding:12px 15px;border-left:3px solid var(--blue);background:#111a2d;color:var(--muted);border-radius:5px}.foot{margin-top:15px;color:var(--muted);font-size:12px}@media(max-width:800px){.cards{grid-template-columns:1fr 1fr}}
</style>
</head>
<body><main>
<h1>HLSL kernel tuning</h1>
<div class="sub">{{{Html(report.Device.AdapterName)}}} · {{{Html(report.Device.DriverVersion)}}} · {{{Html(report.Device.Backend)}}} / SM {{{Html(report.Device.ShaderModel)}}} · {{{Html(report.WorkloadId)}}}</div>
<section class="cards">
<div class="card"><div class="label">Selected</div><div class="value">{{{selection}}}</div></div>
<div class="card"><div class="label">Candidates</div><div class="value">{{{report.Candidates.Count}}}</div></div>
<div class="card"><div class="label">Correct</div><div class="value">{{{report.Candidates.Count(candidate => candidate.Correctness?.Passed == true)}}} / {{{report.Candidates.Count}}}</div></div>
<div class="card"><div class="label">Stable</div><div class="value">{{{report.Candidates.Count(candidate => candidate.Stable)}}} / {{{report.Candidates.Count}}}</div></div>
</section>
<div class="decision">{{{decision}}}</div>
<section class="panel">
<table><thead><tr><th>Compile-time defines</th><th>Gate</th><th>Median ms</th><th>P95 ms</th><th>CV</th><th>M items/s</th><th>vs baseline</th><th>Relative speedup</th><th>RGA evidence</th></tr></thead>
<tbody>{{{rows}}}</tbody></table>
<div class="foot">GPU timestamp intervals contain the complete execution plan: dispatches plus required inter-pass barriers. Upload, compilation, readback, and CPU verification are excluded. RGA data is static compiler evidence, not a substitute for measured GPU time. Green is selected, blue is baseline.</div>
</section>
</main>
<script>
const capture = new URLSearchParams(location.search).get("capture");
if (capture !== null) {
  const progress = Math.max(0, Math.min(1, Number(capture)));
  document.documentElement.style.setProperty("--capture-progress", String(progress));
  document.documentElement.classList.add("capture");
}
</script>
</body></html>
""";
    }

    private static string BuildSvg(TuningRunReport report)
    {
        CandidateResult? baseline = report.Candidates.FirstOrDefault(candidate =>
            candidate.CandidateId == report.BaselineCandidateId);
        double? baselineMedian = baseline?.Timing?.MedianMilliseconds;
        CandidateResult? winner = report.Selection is null
            ? null
            : report.Candidates.FirstOrDefault(candidate => candidate.CandidateId == report.Selection.CandidateId);
        CandidateResult[] ordered = report.Candidates
            .OrderByDescending(candidate => candidate.Timing is null || baselineMedian is null
                ? 0
                : baselineMedian.Value / candidate.Timing.MedianMilliseconds)
            .ToArray();
        double maxSpeedup = ordered.Where(candidate => candidate.Timing is not null && baselineMedian.HasValue)
            .Select(candidate => baselineMedian!.Value / candidate.Timing!.MedianMilliseconds)
            .DefaultIfEmpty(1)
            .Max();
        int height = 150 + ordered.Length * 42;
        StringBuilder svg = new();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"{height}\" viewBox=\"0 0 1200 {height}\">")
            .Append("<rect width=\"1200\" height=\"100%\" fill=\"#0b1020\"/><style>text{font-family:Segoe UI,Arial,sans-serif;fill:#e9eefb}.muted{fill:#99a6bf}.title{font-size:27px;font-weight:700}.small{font-size:13px}.label{font-size:14px}</style>")
            .Append("<text x=\"32\" y=\"42\" class=\"title\">HLSL kernel tuning — relative speedup</text>")
            .Append("<text x=\"32\" y=\"68\" class=\"muted small\">").Append(Xml(report.Device.AdapterName)).Append(" · ")
            .Append(Xml(report.Device.DriverVersion)).Append(" · higher is better</text>");
        for (int index = 0; index < ordered.Length; ++index)
        {
            CandidateResult candidate = ordered[index];
            double? speedup = candidate.Timing is null || baselineMedian is null
                ? null
                : baselineMedian.Value / candidate.Timing.MedianMilliseconds;
            int y = 106 + index * 42;
            double width = speedup is null ? 0 : 650 * speedup.Value / maxSpeedup;
            string color = candidate.CandidateId == winner?.CandidateId
                ? "#4be3a4"
                : candidate.CandidateId == baseline?.CandidateId ? "#5aa9ff" : candidate.Correctness?.Passed == true ? "#7d8aa6" : "#ff6b7a";
            svg.Append("<text x=\"32\" y=\"").Append(y + 16).Append("\" class=\"label\">")
                .Append(Xml(Friendly(candidate.Defines))).Append("</text>")
                .Append("<rect x=\"430\" y=\"").Append(y).Append("\" width=\"").Append(width.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("\" height=\"22\" rx=\"5\" fill=\"").Append(color).Append("\"/>")
                .Append("<text x=\"").Append((445 + width).ToString("0.##", CultureInfo.InvariantCulture)).Append("\" y=\"").Append(y + 16)
                .Append("\" class=\"small\">").Append(speedup is null ? "failed" : Format(speedup.Value) + "× · " + Format(candidate.Timing!.MedianMilliseconds) + " ms")
                .Append("</text>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string Status(CandidateResult candidate)
    {
        if (!candidate.Compiled)
            return "<span class=\"bad\">compile fail</span>";
        if (candidate.Correctness?.Passed != true)
            return "<span class=\"bad\">incorrect</span>";
        return candidate.Stable
            ? "<span class=\"ok\">correct · stable</span>"
            : "<span class=\"bad\">correct · noisy</span>";
    }

    private static string StaticEvidence(CandidateResult candidate)
    {
        IReadOnlyList<RgaPassAnalysis>? passes = candidate.StaticAnalysis?.Passes;
        if (passes is null || passes.Count == 0)
            return "—";
        int? vgprs = NullableMax(passes.Select(pass => pass.VgprsUsed));
        int? available = NullableMax(passes.Select(pass => pass.VgprsAvailable));
        int? live = NullableMax(passes.Select(pass => pass.MaximumLiveVgprs));
        int? allocated = NullableMax(passes.Select(pass => pass.AllocatedVgprs));
        int? sgprs = NullableMax(passes.Select(pass => pass.SgprsUsed));
        int? lds = NullableMax(passes.Select(pass => pass.LdsBytes));
        int? scratch = NullableMax(passes.Select(pass => pass.ScratchBytes));
        double? occupancy = NullableMin(passes.Select(pass => pass.OccupancyWavesPerSimd));
        List<string> values = [];
        if (vgprs.HasValue)
            values.Add($"V {vgprs}/{available?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        if (live.HasValue || allocated.HasValue)
            values.Add($"live {live?.ToString(CultureInfo.InvariantCulture) ?? "?"}→{allocated?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        if (sgprs.HasValue)
            values.Add($"S {sgprs}");
        if (lds > 0)
            values.Add($"LDS {FormatBytes(lds.Value)}");
        if (scratch > 0)
            values.Add($"scratch {FormatBytes(scratch.Value)}");
        if (occupancy.HasValue)
            values.Add($"occupancy {Format(occupancy.Value)} waves/SIMD");
        return values.Count == 0 ? candidate.StaticAnalysis?.Status ?? "—" : string.Join(" · ", values);
    }

    private static string FormatBytes(int bytes) => bytes >= 1024
        ? (bytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " KiB"
        : bytes.ToString(CultureInfo.InvariantCulture) + " B";

    private static string Max(IReadOnlyList<RgaPassAnalysis> passes, Func<RgaPassAnalysis, int?> selector)
    {
        int? value = NullableMax(passes.Select(selector));
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Min(IReadOnlyList<RgaPassAnalysis> passes, Func<RgaPassAnalysis, double?> selector)
    {
        double? value = NullableMin(passes.Select(selector));
        return value.HasValue ? Format(value.Value) : string.Empty;
    }

    private static int? NullableMax(IEnumerable<int?> values)
    {
        int[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static double? NullableMin(IEnumerable<double?> values)
    {
        double[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }

    private static string Friendly(IReadOnlyDictionary<string, int> defines) => string.Join(" · ",
        defines.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            $"{FriendlyDefineName(pair.Key)}={pair.Value}"));

    private static string FriendlyDefineName(string name) => name switch
    {
        "HLSLPERF_ELEMENTS_PER_THREAD" => "EPT",
        "HLSLPERF_GROUP_SIZE" => "group",
        "HLSLPERF_TILE_DIM" => "tile",
        "HLSLPERF_BLOCK_ROWS" => "rows",
        _ => name.Replace("HLSLPERF_", string.Empty, StringComparison.Ordinal).ToLowerInvariant()
    };

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Html(string value) => WebUtility.HtmlEncode(value);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
