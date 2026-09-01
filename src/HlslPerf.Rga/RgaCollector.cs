using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HlslPerf.Core;

namespace HlslPerf.Rga;

public sealed partial class RgaCollector
{
    private readonly RgaInstallation installation;

    public RgaCollector(RgaInstallation installation) => this.installation = installation;

    public TuningRunReport Attach(
        TuningRunReport report,
        TuningManifest manifest,
        IKernelWorkload workload,
        string manifestPath,
        string outputDirectory,
        string? target = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string kernelPath = Path.GetFullPath(Path.Combine(manifestDirectory, manifest.KernelPath));
        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        List<CandidateResult> candidates = new(report.Candidates.Count);
        foreach (CandidateResult result in report.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Compiled || result.Correctness?.Passed != true)
            {
                candidates.Add(result);
                continue;
            }
            KernelCandidate candidate = new(result.Defines);
            KernelExecutionPlan plan = workload.Build(manifest, candidate);
            progress?.Invoke($"RGA {candidate.Id}");
            RgaCandidateAnalysis analysis = AnalyzeCandidate(
                manifest,
                candidate,
                plan,
                kernelPath,
                Path.Combine(root, Sanitize(candidate.Id)),
                target,
                cancellationToken);
            candidates.Add(result with { StaticAnalysis = analysis });
        }
        return report with { Candidates = candidates };
    }

    private RgaCandidateAnalysis AnalyzeCandidate(
        TuningManifest manifest,
        KernelCandidate candidate,
        KernelExecutionPlan plan,
        string kernelPath,
        string candidateDirectory,
        string? target,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(candidateDirectory);
        List<RgaPassAnalysis> analyses = [];
        List<string> diagnostics = [];
        string? resolvedTarget = target;
        foreach (KernelPassSpec pass in plan.Passes
                     .GroupBy(value => value.EntryPoint, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stem = Sanitize(pass.EntryPoint);
            string statisticsTemplate = Path.Combine(candidateDirectory, $"{stem}-stats.txt");
            string isaTemplate = Path.Combine(candidateDirectory, $"{stem}-isa.txt");
            string liveTemplate = Path.Combine(candidateDirectory, $"{stem}-livereg.txt");
            ProcessResult process = Run(
                BuildArguments(manifest, candidate, pass.EntryPoint, kernelPath, statisticsTemplate, isaTemplate, liveTemplate, target),
                cancellationToken);
            diagnostics.Add($"[{pass.EntryPoint}] exit={process.ExitCode}: {Compact(process.Output)}");
            resolvedTarget ??= TargetRegex().Match(process.Output) is { Success: true } match
                ? match.Value.ToLowerInvariant()
                : null;

            string? statsPath = FindGenerated(candidateDirectory, stem, "resourceUsage.numUsedVgprs");
            string? livePath = FindGenerated(candidateDirectory, stem, "Maximum # VGPR");
            string? isaPath = Directory.EnumerateFiles(candidateDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Contains(stem, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(path => path != statsPath && path != livePath && File.ReadAllText(path).Contains("Disassembly", StringComparison.OrdinalIgnoreCase));
            if (process.ExitCode != 0 || statsPath is null)
                continue;
            analyses.Add(RgaStatisticsParser.Parse(
                pass.Name,
                pass.EntryPoint,
                File.ReadAllText(statsPath),
                livePath is null ? null : File.ReadAllText(livePath),
                isaPath,
                livePath));
        }

        return new RgaCandidateAnalysis(
            "AMD Radeon GPU Analyzer DX12 live-driver",
            installation.Version,
            resolvedTarget,
            analyses.Count > 0 ? "collected" : "failed",
            analyses,
            string.Join(Environment.NewLine, diagnostics));
    }

    public IReadOnlyList<string> BuildArguments(
        TuningManifest manifest,
        KernelCandidate candidate,
        string entryPoint,
        string kernelPath,
        string statisticsPath,
        string isaPath,
        string liveVgprPath,
        string? target)
    {
        List<string> arguments =
        [
            "-s", "dx12",
            "--cs", kernelPath,
            "--cs-entry", entryPoint,
            "--cs-model", $"cs_{manifest.ShaderModel}",
            "--analysis", statisticsPath,
            "--isa", isaPath,
            "--livereg", liveVgprPath
        ];
        if (!string.IsNullOrWhiteSpace(target))
        {
            arguments.Add("--asic");
            arguments.Add(target);
        }
        foreach ((string name, int value) in candidate.Defines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            arguments.Add("--define");
            arguments.Add($"{name}={value.ToString(CultureInfo.InvariantCulture)}");
        }
        return arguments;
    }

    private ProcessResult Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(installation.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start RGA.");
        StringBuilder output = new();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        while (!process.WaitForExit(250))
        {
            if (!cancellationToken.IsCancellationRequested)
                continue;
            process.Kill(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static string? FindGenerated(string directory, string stem, string requiredText)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetFileName(path).Contains(stem, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (File.ReadAllText(path).Contains(requiredText, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    private static string Compact(string value)
    {
        string compact = string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 800 ? compact : compact[..800] + "…";
    }

    private static string Sanitize(string value) => new(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());

    [GeneratedRegex(@"gfx\d+")]
    private static partial Regex TargetRegex();

    private sealed record ProcessResult(int ExitCode, string Output);
}
