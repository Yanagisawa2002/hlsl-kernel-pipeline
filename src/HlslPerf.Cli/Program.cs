using System.Globalization;
using System.Text;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;
using HlslPerf.Rga;

namespace HlslPerf.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return args.Length == 0 || args[0] is "-h" or "--help" or "help"
                ? ShowHelp()
                : args[0] is "-v" or "--version" or "version"
                    ? ShowVersion()
                : args[0] switch
                {
                    "doctor" => Doctor(args[1..]),
                    "workloads" => ListWorkloads(args[1..]),
                    "new-workload" => NewWorkload(args[1..]),
                    "tune" => Tune(args[1..]),
                    "render" => Render(args[1..]),
                    _ => UsageError($"Unknown command '{args[0]}'.")
                };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            if (Environment.GetEnvironmentVariable("HLSLPERF_TRACE") == "1")
                Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int ShowVersion()
    {
        Console.WriteLine(HlslPerfSdk.Version);
        return 0;
    }

    private static int NewWorkload(string[] args)
    {
        string? target = null;
        string? workloadId = null;
        string? className = null;
        for (int index = 0; index < args.Length; ++index)
        {
            switch (args[index])
            {
                case "--id":
                    workloadId = RequireValue(args, ref index, "--id");
                    break;
                case "--class":
                    className = RequireValue(args, ref index, "--class");
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal) || target is not null)
                        throw new ArgumentException($"Unexpected new-workload argument '{args[index]}'.");
                    target = args[index];
                    break;
            }
        }
        if (target is null)
            throw new ArgumentException("new-workload requires a target directory.");
        string leaf = Path.GetFileName(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar));
        workloadId ??= ToKebabCase(leaf) + "-v1";
        className ??= ToClassName(leaf);
        IReadOnlyList<string> files = WorkloadScaffolder.Create(target, workloadId, className);
        Console.WriteLine($"Created workload plugin '{workloadId}' in {Path.GetFullPath(target)}");
        foreach (string file in files)
            Console.WriteLine($"  {Path.GetFileName(file)}");
        return 0;
    }

    private static string ToKebabCase(string value)
    {
        StringBuilder result = new();
        foreach (char character in value)
        {
            if (char.IsUpper(character) && result.Length > 0 && result[^1] != '-')
                result.Append('-');
            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }
        return result.ToString().Trim('-');
    }

    private static string ToClassName(string value)
    {
        string[] parts = value.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '-', '_', '.', ' '],
            StringSplitOptions.RemoveEmptyEntries);
        string result = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (string.IsNullOrEmpty(result) || !char.IsLetter(result[0]))
            result = "Custom" + result;
        return result;
    }

    private static int ListWorkloads(string[] args)
    {
        ParsedOptions options = ParseOptions(args, requiresManifest: false);
        WorkloadCatalog catalog = CreateWorkloadCatalog(options);
        foreach (string workloadId in catalog.WorkloadIds)
            Console.WriteLine(workloadId);
        return 0;
    }

    private static int Doctor(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("HlslPerf's D3D12 runner requires Windows 10 or later.");
        ParsedOptions options = ParseOptions(args, requiresManifest: false);
        using D3D12Tuner tuner = new(options.Adapter);
        Console.WriteLine(JsonSerializer.Serialize(tuner.DescribeDevice(), JsonDefaults.Options));
        return 0;
    }

    private static int Tune(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("HlslPerf's D3D12 runner requires Windows 10 or later.");
        ParsedOptions options = ParseOptions(args, requiresManifest: true);
        string manifestPath = Path.GetFullPath(options.ManifestPath!);
        TuningManifest manifest = TuningManifest.Load(manifestPath);
        string outputDirectory = options.OutputDirectory is null
            ? options.ResumePath is not null
                ? Path.GetDirectoryName(Path.GetFullPath(options.ResumePath))!
                : Path.Combine(
                    Directory.GetCurrentDirectory(),
                    ".hlslperf",
                    "runs",
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture))
            : Path.GetFullPath(options.OutputDirectory);
        string checkpointPath = Path.GetFullPath(
            options.ResumePath ?? options.CheckpointPath ?? Path.Combine(outputDirectory, "checkpoint.json"));

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using D3D12Tuner tuner = new(options.Adapter);
            DeviceFingerprint device = tuner.DescribeDevice(manifest.ShaderModel);
            IKernelWorkload workload = CreateWorkloadCatalog(options).Resolve(manifest);
            Console.WriteLine($"Adapter: {device.AdapterName}");
            Console.WriteLine($"Driver:  {device.DriverVersion}");
            Console.WriteLine($"Run:     {manifest.Name}");
            Console.WriteLine();

            TuningRunReport report = tuner.Run(
                manifest,
                manifestPath,
                workload,
                progress => PrintProgress(progress),
                cancellation.Token,
                Path.Combine(Directory.GetCurrentDirectory(), ".hlslperf", "cache", "dxil"),
                null,
                new TuningCheckpointOptions(checkpointPath, options.ResumePath is not null));
            if (!string.Equals(options.Rga, "off", StringComparison.OrdinalIgnoreCase))
            {
                string? explicitRgaPath = string.Equals(options.Rga, "auto", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : options.Rga;
                RgaInstallation? installation = RgaLocator.Find(explicitRgaPath);
                if (installation is null && explicitRgaPath is not null)
                    throw new FileNotFoundException($"RGA was not found at '{explicitRgaPath}'.");
                if (installation is null)
                {
                    Console.WriteLine("RGA:     optional analyzer not found; static evidence was not collected.");
                }
                else
                {
                    Console.WriteLine($"RGA:     {installation.Version ?? installation.ExecutablePath}");
                    RgaCollector collector = new(installation);
                    report = collector.Attach(
                        report,
                        manifest,
                        workload,
                        manifestPath,
                        Path.Combine(outputDirectory, "rga"),
                        options.RgaTarget,
                        message => Console.WriteLine($"          {message}"),
                        cancellation.Token);
                }
            }
            ReportArtifacts artifacts = ReportWriter.Write(report, outputDirectory);
            PrintSummary(report, artifacts);
            return artifacts.ProfileJsonPath is null ? 2 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int Render(string[] args)
    {
        ParsedOptions options = ParseOptions(args, requiresManifest: true);
        if (options.Adapter is not null)
            throw new ArgumentException("render does not accept --adapter.");
        string runPath = Path.GetFullPath(options.ManifestPath!);
        TuningRunReport report = JsonSerializer.Deserialize<TuningRunReport>(
            File.ReadAllText(runPath),
            JsonDefaults.Options) ?? throw new InvalidDataException($"Could not deserialize run '{runPath}'.");
        string outputDirectory = options.OutputDirectory is null
            ? Path.GetDirectoryName(runPath)!
            : Path.GetFullPath(options.OutputDirectory);
        ReportArtifacts artifacts = ReportWriter.Write(report, outputDirectory);
        Console.WriteLine($"Report:  {artifacts.HtmlPath}");
        Console.WriteLine($"SVG:     {artifacts.SvgPath}");
        Console.WriteLine($"Profile: {artifacts.ProfileJsonPath ?? "not emitted"}");
        return 0;
    }

    private static void PrintProgress(TuningProgress progress)
    {
        string prefix = $"[{progress.CandidateIndex,2}/{progress.CandidateCount}]";
        if (progress.Stage == "compile")
        {
            Console.Write($"{prefix} {progress.CandidateId} ... ");
            return;
        }
        if (progress.Stage == "measure")
            return;

        CandidateResult result = progress.Result!;
        if (progress.Stage == "resumed")
        {
            Console.WriteLine(
                $"{prefix} {progress.CandidateId} ... resumed " +
                $"{result.Timing!.MedianMilliseconds:0.####} ms | " +
                $"{(result.Correctness?.Passed == true ? "correct" : "INCORRECT")} | " +
                $"{(result.Stable ? "stable" : "noisy")}");
            return;
        }
        if (result.Timing is null)
        {
            Console.WriteLine($"FAILED ({result.Error})");
            return;
        }
        Console.WriteLine(
            $"{result.Timing.MedianMilliseconds,8:0.####} ms median | " +
            $"{result.Timing.P95Milliseconds,8:0.####} ms p95 | " +
            $"CV {result.Timing.CoefficientOfVariation,6:P1} | " +
            $"N {result.MeasuredDispatchesPerBatch,3} | " +
            $"{(result.Correctness?.Passed == true ? "correct" : "INCORRECT")} | " +
            $"{(result.Stable ? "stable" : "noisy")}");
    }

    private static void PrintSummary(TuningRunReport report, ReportArtifacts artifacts)
    {
        Console.WriteLine();
        if (report.Selection is null)
        {
            Console.WriteLine("No candidate passed all required gates.");
        }
        else
        {
            CandidateResult selected = report.Candidates.Single(candidate =>
                candidate.CandidateId == report.Selection.CandidateId);
            CandidateResult? baseline = report.Candidates.FirstOrDefault(candidate =>
                candidate.CandidateId == report.BaselineCandidateId);
            double? speedup = baseline?.Timing is null
                ? null
                : baseline.Timing.MedianMilliseconds / selected.Timing!.MedianMilliseconds;
            Console.WriteLine($"Selected: {selected.CandidateId}");
            if (report.Selection.RetainedBaseline)
                Console.WriteLine($"Observed: {report.Selection.ObservedFastestCandidateId} (deployment guard did not clear)");
            Console.WriteLine($"Median:   {selected.Timing!.MedianMilliseconds:0.####} ms");
            Console.WriteLine($"P95:      {selected.Timing.P95Milliseconds:0.####} ms");
            Console.WriteLine($"Speedup:  {(speedup.HasValue ? $"{speedup.Value:0.###}× vs baseline" : "baseline unavailable")}");
            Console.WriteLine($"Decision: {report.Selection.Reason}");
        }
        Console.WriteLine($"Report:   {artifacts.HtmlPath}");
        Console.WriteLine($"Profile:  {artifacts.ProfileJsonPath ?? "not emitted"}");
        Console.WriteLine($"Raw data: {artifacts.RunJsonPath}");
    }

    private static ParsedOptions ParseOptions(string[] args, bool requiresManifest)
    {
        string? manifest = null;
        string? output = null;
        string? adapter = null;
        string rga = "auto";
        string? rgaTarget = null;
        List<string> pluginAssemblies = [];
        List<string> pluginDirectories = [];
        string? checkpointPath = null;
        string? resumePath = null;
        for (int index = 0; index < args.Length; ++index)
        {
            string value = args[index];
            switch (value)
            {
                case "--output":
                    output = RequireValue(args, ref index, value);
                    break;
                case "--adapter":
                    adapter = RequireValue(args, ref index, value);
                    break;
                case "--rga":
                    rga = RequireValue(args, ref index, value);
                    break;
                case "--rga-target":
                    rgaTarget = RequireValue(args, ref index, value);
                    break;
                case "--plugin":
                    pluginAssemblies.Add(RequireValue(args, ref index, value));
                    break;
                case "--plugin-dir":
                    pluginDirectories.Add(RequireValue(args, ref index, value));
                    break;
                case "--checkpoint":
                    checkpointPath = RequireValue(args, ref index, value);
                    break;
                case "--resume":
                    resumePath = RequireValue(args, ref index, value);
                    break;
                default:
                    if (value.StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option '{value}'.");
                    if (!requiresManifest || manifest is not null)
                        throw new ArgumentException($"Unexpected argument '{value}'.");
                    manifest = value;
                    break;
            }
        }
        if (requiresManifest && manifest is null)
            throw new ArgumentException("tune requires a manifest path.");
        if (checkpointPath is not null && resumePath is not null)
            throw new ArgumentException("Use either --checkpoint or --resume, not both.");
        return new ParsedOptions(
            manifest,
            output,
            adapter,
            rga,
            rgaTarget,
            pluginAssemblies,
            pluginDirectories,
            checkpointPath,
            resumePath);
    }

    private static WorkloadCatalog CreateWorkloadCatalog(ParsedOptions options)
    {
        WorkloadCatalog catalog = new();
        catalog.Register(new BuiltinWorkloadProvider(), "built-in workload pack");
        IEnumerable<string> explicitAssemblyPaths = options.PluginAssemblies
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string assemblyPath in explicitAssemblyPaths)
            foreach (IKernelWorkloadProvider provider in WorkloadPluginLoader.Load(assemblyPath))
                catalog.Register(provider, assemblyPath);

        IEnumerable<string> discoveredAssemblyPaths = options.PluginDirectories
            .SelectMany(WorkloadPluginLoader.DiscoverAssemblies)
            .Select(Path.GetFullPath)
            .Except(explicitAssemblyPaths, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string assemblyPath in discoveredAssemblyPaths)
            foreach (IKernelWorkloadProvider provider in WorkloadPluginLoader.LoadIfPresent(assemblyPath))
                catalog.Register(provider, assemblyPath);
        return catalog;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static int ShowHelp()
    {
        Console.WriteLine(
            $$"""
            HlslPerf {{HlslPerfSdk.Version}}

              hlslperf doctor [--adapter <name-fragment>]
              hlslperf workloads [--plugin <assembly>] [--plugin-dir <directory>]
              hlslperf new-workload <directory> [--id <workload-id>] [--class <ClassName>]
              hlslperf tune <manifest.json> [--adapter <name-fragment>] [--output <directory>]
                            [--rga <auto|off|path>] [--rga-target <gfx-id>]
                            [--plugin <assembly>] [--plugin-dir <directory>]
                            [--checkpoint <path> | --resume <checkpoint.json>]
              hlslperf render <run.json> [--output <directory>]

            doctor verifies D3D12 device creation and prints the profile fingerprint.
            tune compiles candidates, rejects incorrect output, measures GPU timestamps,
            optionally attaches RGA live-driver evidence, and writes JSON, CSV, SVG, HTML,
            and a selected device profile. RGA defaults to auto-discovery and is never required.
            render rebuilds presentation artifacts from an existing immutable run.
            """);
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run with --help for usage.");
        return 64;
    }

    private sealed record ParsedOptions(
        string? ManifestPath,
        string? OutputDirectory,
        string? Adapter,
        string Rga,
        string? RgaTarget,
        IReadOnlyList<string> PluginAssemblies,
        IReadOnlyList<string> PluginDirectories,
        string? CheckpointPath,
        string? ResumePath);
}
