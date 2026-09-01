using System.Globalization;
using System.Text;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;

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
                : args[0] switch
                {
                    "doctor" => Doctor(args[1..]),
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

    private static int Doctor(string[] args)
    {
        ParsedOptions options = ParseOptions(args, requiresManifest: false);
        using D3D12Tuner tuner = new(options.Adapter);
        Console.WriteLine(JsonSerializer.Serialize(tuner.DescribeDevice(), JsonDefaults.Options));
        return 0;
    }

    private static int Tune(string[] args)
    {
        ParsedOptions options = ParseOptions(args, requiresManifest: true);
        string manifestPath = Path.GetFullPath(options.ManifestPath!);
        TuningManifest manifest = TuningManifest.Load(manifestPath);
        string outputDirectory = options.OutputDirectory is null
            ? Path.Combine(
                Directory.GetCurrentDirectory(),
                ".hlslperf",
                "runs",
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture))
            : Path.GetFullPath(options.OutputDirectory);

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
            Console.WriteLine($"Adapter: {device.AdapterName}");
            Console.WriteLine($"Driver:  {device.DriverVersion}");
            Console.WriteLine($"Run:     {manifest.Name}");
            Console.WriteLine();

            TuningRunReport report = tuner.Run(
                manifest,
                manifestPath,
                progress => PrintProgress(progress),
                cancellation.Token,
                Path.Combine(Directory.GetCurrentDirectory(), ".hlslperf", "cache", "dxil"));
            ReportArtifacts artifacts = ReportWriter.Write(report, outputDirectory);
            PrintSummary(report, artifacts);
            return report.Selection is null ? 2 : 0;
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
        return new ParsedOptions(manifest, output, adapter);
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
            """
            HlslKernelPipeline

              hlslperf doctor [--adapter <name-fragment>]
              hlslperf tune <manifest.json> [--adapter <name-fragment>] [--output <directory>]
              hlslperf render <run.json> [--output <directory>]

            doctor verifies D3D12 device creation and prints the profile fingerprint.
            tune compiles candidates, rejects incorrect output, measures GPU timestamps,
            and writes JSON, CSV, SVG, HTML, and a selected device profile.
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

    private sealed record ParsedOptions(string? ManifestPath, string? OutputDirectory, string? Adapter);
}
