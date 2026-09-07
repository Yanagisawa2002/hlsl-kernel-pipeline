using System.Globalization;
using System.Text.RegularExpressions;
using HlslPerf.Core;

namespace HlslPerf.Rga;

public static partial class RgaStatisticsParser
{
    public static RgaPassAnalysis Parse(
        string passName,
        string entryPoint,
        string statisticsText,
        string? liveVgprText = null,
        string? isaPath = null,
        string? liveVgprPath = null)
    {
        Dictionary<string, double> values = [];
        foreach (string line in statisticsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = line.IndexOf('=');
            if (equals < 0)
                continue;
            string key = Normalize(line[..equals]);
            string rawValue = line[(equals + 1)..].Trim();
            if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                if (double.IsFinite(value) && value >= 0) values[key] = value;
        }

        int? maximumLive = null;
        int? allocated = null;
        if (!string.IsNullOrWhiteSpace(liveVgprText))
        {
            Match match = LiveVgprSummary().Match(liveVgprText);
            if (match.Success)
            {
                maximumLive = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                allocated = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }

        int? vgprsUsed = Int(values, "resourceusagenumusedvgprs");
        int? vgprsAvailable = Int(values, "numavailablevgprs");
        int? sgprsUsed = Int(values, "resourceusagenumusedsgprs");
        int? sgprsAvailable = Int(values, "numavailablesgprs");
        return new RgaPassAnalysis(
            passName,
            entryPoint,
            vgprsUsed,
            vgprsAvailable,
            Int(values, "numphysicalvgprs"),
            maximumLive,
            allocated,
            Ratio(vgprsUsed, vgprsAvailable),
            sgprsUsed,
            sgprsAvailable,
            Int(values, "numphysicalsgprs"),
            Ratio(sgprsUsed, sgprsAvailable),
            Int(values, "resourceusageldsusagesizeinbytes"),
            Int(values, "resourceusageldssizeperlocalworkgroup"),
            Int(values, "resourceusagescratchmemusageinbytes"),
            Int(values, "computeworkgroupsizex"),
            Int(values, "computeworkgroupsizey"),
            Int(values, "computeworkgroupsizez"),
            Double(values, "occupancywavespersimd"),
            isaPath,
            liveVgprPath)
        {
            VgprSpills = Int(values, "resourceusagenumvgprspills"),
            SgprSpills = Int(values, "resourceusagenumsgprspills")
        };
    }

    private static string Normalize(string key) => new(key
        .Where(character => char.IsLetterOrDigit(character))
        .Select(char.ToLowerInvariant)
        .ToArray());

    private static int? Int(IReadOnlyDictionary<string, double> values, string key) =>
        values.TryGetValue(key, out double value) && value <= int.MaxValue && value == Math.Truncate(value) ? (int)value : null;

    private static double? Double(IReadOnlyDictionary<string, double> values, string key) =>
        values.TryGetValue(key, out double value) ? value : null;

    private static double? Ratio(int? used, int? available) => used.HasValue && available > 0
        ? (double)used.Value / available.Value
        : null;

    [GeneratedRegex(@"Maximum\s*#\s*VGPR\s*used\s+(\d+)\s*,\s*(?:#\s*VGPR\s*allocated|VGPRs\s*allocated\s*by\s*HW)\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LiveVgprSummary();
}
