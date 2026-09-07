using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
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
        if (HlslSourceGraph.Load(kernelPath).CombinedSha256 != report.KernelSha256)
            throw new InvalidDataException("RGA source changed since measurement; refusing an incomparable evidence attachment.");
        if (report.Device.ShaderModel != manifest.ShaderModel)
            throw new InvalidDataException("RGA shader model differs from the measured shader model.");
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
                cancellationToken,
                report.Device);
            candidates.Add(result with { StaticAnalysis = analysis });
        }
        return report with { Candidates = candidates };
    }

    public RgaCandidateAnalysis AnalyzeCandidate(
        TuningManifest manifest,
        KernelCandidate candidate,
        KernelExecutionPlan plan,
        string kernelPath,
        string candidateDirectory,
        string? target,
        CancellationToken cancellationToken = default,
        DeviceFingerprint? measuredDevice = null)
    {
        Directory.CreateDirectory(candidateDirectory);
        List<RgaPassAnalysis> analyses = [];
        List<string> diagnostics = [];
        string? resolvedTarget = target;
        HlslSourceGraph sourceGraph = HlslSourceGraph.Load(kernelPath);
        foreach (KernelPassSpec pass in plan.Passes
                     .GroupBy(value => value.EntryPoint, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stem = Sanitize(pass.EntryPoint);
            string invocationDirectory = Path.Combine(candidateDirectory, stem, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(invocationDirectory);
            for (int sourceIndex = 0; sourceIndex < sourceGraph.Dependencies.Count; sourceIndex++)
            {
                HlslSourceDependency source = sourceGraph.Dependencies[sourceIndex];
                File.Copy(source.FullPath, Path.Combine(invocationDirectory, $"source-{sourceIndex}-{Path.GetFileName(source.FullPath)}"));
            }
            string statisticsTemplate = Path.Combine(invocationDirectory, $"{stem}-stats.txt");
            string isaTemplate = Path.Combine(invocationDirectory, $"{stem}-isa.txt");
            string liveTemplate = Path.Combine(invocationDirectory, $"{stem}-livereg.txt");
            List<string> arguments = BuildArguments(manifest, candidate, pass.EntryPoint, kernelPath, statisticsTemplate, isaTemplate, liveTemplate, target).ToList();
            foreach (string directory in sourceGraph.IncludeDirectories) { arguments.Add("-I"); arguments.Add(directory); }
            string dxcDirectory = Path.Combine(Path.GetDirectoryName(installation.ExecutablePath)!, "utils", "dx12", "dxc");
            List<EvidenceFile> compilerFiles = [];
            if (Directory.Exists(dxcDirectory))
            {
                arguments.Add("--dxc"); arguments.Add(dxcDirectory);
                compilerFiles.AddRange(Directory.EnumerateFiles(dxcDirectory).Where(path =>
                    Path.GetExtension(path) is ".exe" or ".dll").Select(DescribeFile));
            }
            arguments.Add("--dxc-opt"); arguments.Add("-O3 -Ges -WX");
            arguments.Add("--binary"); arguments.Add(Path.Combine(invocationDirectory, stem + ".bin"));
            DateTimeOffset started = DateTimeOffset.UtcNow;
            ProcessResult process = Run(arguments, cancellationToken);
            File.WriteAllText(Path.Combine(invocationDirectory, "process-output.txt"), process.Output);
            diagnostics.Add($"[{pass.EntryPoint}] exit={process.ExitCode}: {Compact(process.Output)}");
            resolvedTarget ??= TargetRegex().Match(process.Output) is { Success: true } match
                ? match.Value.ToLowerInvariant()
                : null;

            string? statsPath = FindGenerated(invocationDirectory, stem, "resourceUsage.numUsedVgprs");
            string? livePath = FindGenerated(invocationDirectory, stem, "Maximum # VGPR");
            string? isaPath = Directory.EnumerateFiles(invocationDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Contains(stem, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(path => path != statsPath && path != livePath && File.ReadAllText(path).Contains("Disassembly", StringComparison.OrdinalIgnoreCase));
            RgaInvocationEvidence evidence = new("hlslperf.rga-invocation.v1", started, DateTimeOffset.UtcNow,
                DescribeFile(installation.ExecutablePath), compilerFiles, arguments, pass.EntryPoint,
                manifest.ShaderModel, candidate.Defines, sourceGraph.CombinedSha256, sourceGraph.Dependencies,
                measuredDevice, process.ExitCode, Directory.EnumerateFiles(invocationDirectory).Select(DescribeFile).ToArray(),
                "RGA recompiles the identified source and defines with its identified DXC through the live driver; not asserted byte-identical to measured DXIL.");
            File.WriteAllText(Path.Combine(invocationDirectory, "invocation.json"), JsonSerializer.Serialize(evidence, JsonDefaults.Options));
            analyses.Add(RgaStatisticsParser.Parse(
                pass.Name,
                pass.EntryPoint,
                statsPath is null || process.ExitCode != 0 ? "" : File.ReadAllText(statsPath),
                livePath is null || process.ExitCode != 0 ? null : File.ReadAllText(livePath),
                isaPath,
                livePath) with { Evidence = evidence, StatisticsPath = statsPath,
                    Status = process.ExitCode == 0 && statsPath is not null ? "collected" : "unavailable" });
        }

        return new RgaCandidateAnalysis(
            "AMD Radeon GPU Analyzer DX12 live-driver",
            installation.Version,
            resolvedTarget,
            analyses.All(pass => pass.Status == "collected") && analyses.Count > 0 ? "collected" :
                analyses.Any(pass => pass.Status == "collected") ? "partial" : "failed",
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
        process.OutputDataReceived += (_, args) => { lock (output) { if (args.Data is not null) output.AppendLine(args.Data); } };
        process.ErrorDataReceived += (_, args) => { lock (output) { if (args.Data is not null) output.AppendLine(args.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Stopwatch timeout = Stopwatch.StartNew();
        while (!process.WaitForExit(250))
        {
            if (!cancellationToken.IsCancellationRequested && timeout.Elapsed < TimeSpan.FromMinutes(2))
                continue;
            process.Kill(true);
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            output.AppendLine("RGA exceeded the bounded two-minute per-entrypoint timeout.");
            return new ProcessResult(-1, output.ToString());
        }
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static EvidenceFile DescribeFile(string path) => new(Path.GetFullPath(path),
        ContentHash.Sha256(File.ReadAllBytes(path)), new FileInfo(path).Length,
        Path.GetExtension(path) is ".exe" or ".dll" ? FileVersionInfo.GetVersionInfo(path).FileVersion : null);

    private static string? FindGenerated(string directory, string stem, string requiredText)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
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
