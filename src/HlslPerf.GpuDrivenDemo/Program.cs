using System.Globalization;
using System.Text;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.GpuDriven;
using Vortice.Dxc;

namespace HlslPerf.GpuDrivenDemo;

internal static class Program
{
    private static readonly string[] MeasuredLevels = ["low", "medium", "high", "extreme"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return ShowHelp();
            string repositoryRoot = FindRepositoryRoot();
            return args[0] switch
            {
                "validate" => ValidateCpuAndShaders(repositoryRoot, args[1..]),
                "run" => RunGpuEvidence(repositoryRoot, ParseRunOptions(args[1..])),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Use validate or run.")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. The per-level checkpoint can be resumed explicitly.");
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

    private static int ValidateCpuAndShaders(string repositoryRoot, string[] args)
    {
        if (args.Length != 0)
            throw new ArgumentException("validate does not accept options.");

        Console.WriteLine("Validation mode: CPU plan/oracle + DXC compile only (no D3D12 device is created).\n");
        foreach (string level in MeasuredLevels)
        {
            string path = ManifestPath(repositoryRoot, level);
            TuningManifest manifest = TuningManifest.Load(path);
            IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
            KernelCandidate baseline = CandidateGenerator.ResolveBaseline(manifest, candidates);
            Console.WriteLine($"{level,-7} manifest: {candidates.Count,3} candidates · baseline {ShortCandidate(baseline.Defines)}");
        }

        string smokePath = ManifestPath(repositoryRoot, "smoke");
        TuningManifest smoke = TuningManifest.Load(smokePath);
        IReadOnlyList<KernelCandidate> smokeCandidates = CandidateGenerator.Expand(smoke);
        HlslSourceGraph sourceGraph = HlslSourceGraph.Load(Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(smokePath)!, smoke.KernelPath)));
        string? expectedHash = null;
        foreach (KernelCandidate candidate in smokeCandidates)
        {
            CrowdVfxWorkload workload = new();
            KernelExecutionPlan plan = workload.Build(smoke, candidate);
            expectedHash ??= plan.ExpectedSha256;
            if (!string.Equals(expectedHash, plan.ExpectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Smoke candidates produced different CPU oracle identities.");
            CompilePlan(sourceGraph, smoke, candidate, plan);
            Console.WriteLine(
                $"smoke  {ShortCandidate(candidate.Defines),-48} " +
                $"{plan.Passes.Count,2} passes · {workload.Scene.VisibleCounts.Sum(),4} visible-frame instances · DXC passed");
        }

        TuningManifest coverageManifest = TuningManifest.Load(ManifestPath(repositoryRoot, "low"));
        IReadOnlyList<KernelCandidate> coverageCandidates = CandidateGenerator.Expand(coverageManifest);
        KernelCandidate[] compileCoverage =
        [
            CandidateGenerator.ResolveBaseline(coverageManifest, coverageCandidates),
            FindCoverageCandidate(coverageCandidates, new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["HLSLPERF_CROWD_BACKEND"] = 2,
                ["HLSLPERF_GROUP_SIZE"] = 128,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
                ["HLSLPERF_VECTOR_WIDTH"] = 1,
                ["HLSLPERF_WAVE_SIZE"] = 32,
                ["HLSLPERF_SINGLE_PASS_ITEMS_SCALE"] = 2,
                ["HLSLPERF_SINGLE_PASS_GROUPS"] = 128,
                ["HLSLPERF_CROWD_TILE_REPLICAS"] = 1
            }),
            FindCoverageCandidate(coverageCandidates, new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["HLSLPERF_CROWD_BACKEND"] = 2,
                ["HLSLPERF_GROUP_SIZE"] = 512,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
                ["HLSLPERF_VECTOR_WIDTH"] = 4,
                ["HLSLPERF_WAVE_SIZE"] = 64,
                ["HLSLPERF_SINGLE_PASS_ITEMS_SCALE"] = 4,
                ["HLSLPERF_SINGLE_PASS_GROUPS"] = 256,
                ["HLSLPERF_CROWD_TILE_REPLICAS"] = 2
            })
        ];
        CrowdVfxWorkload coverageWorkload = new();
        foreach (KernelCandidate candidate in compileCoverage)
        {
            KernelExecutionPlan plan = coverageWorkload.Build(coverageManifest, candidate);
            CompilePlan(sourceGraph, coverageManifest, candidate, plan);
            Console.WriteLine($"cover  {ShortCandidate(candidate.Defines),-48} 512-bin entry points · DXC passed");
        }

        Console.WriteLine($"\nCPU oracle SHA-256: {expectedHash}");
        Console.WriteLine($"HLSL dependency hash: {sourceGraph.CombinedSha256}");
        Console.WriteLine("Validation complete; no adapter was opened and no GPU work was submitted.");
        return 0;
    }

    private static KernelCandidate FindCoverageCandidate(
        IReadOnlyList<KernelCandidate> candidates,
        IReadOnlyDictionary<string, int> required) => candidates.Single(candidate =>
        required.All(pair => candidate.Defines.TryGetValue(pair.Key, out int value) && value == pair.Value));

    private static void CompilePlan(
        HlslSourceGraph sourceGraph,
        TuningManifest manifest,
        KernelCandidate candidate,
        KernelExecutionPlan plan)
    {
        DxcDefine[] defines = candidate.Defines
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DxcDefine
            {
                Name = pair.Key,
                Value = pair.Value.ToString(CultureInfo.InvariantCulture)
            })
            .ToArray();
        DxcCompilerOptions options = new()
        {
            ShaderModel = ParseShaderModel(manifest.ShaderModel),
            OptimizationLevel = 3,
            EnableStrictness = true,
            WarningsAreErrors = true
        };
        string[] includeArguments = sourceGraph.IncludeDirectories
            .SelectMany(directory => new[] { "-I", directory })
            .ToArray();
        foreach (string entryPoint in plan.Passes.Select(pass => pass.EntryPoint).Distinct(StringComparer.Ordinal))
        {
            using IDxcResult result = DxcCompiler.Compile(
                DxcShaderStage.Compute,
                sourceGraph.RootSource,
                entryPoint,
                options,
                sourceGraph.RootPath,
                defines,
                null,
                includeArguments);
            string diagnostics = result.GetErrors();
            if (result.GetStatus().Failure)
                throw new InvalidDataException(
                    $"DXC rejected {entryPoint} for {candidate.Id}:{Environment.NewLine}{diagnostics}");
        }
    }

    private static int RunGpuEvidence(string repositoryRoot, RunOptions options)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("The Crowd/VFX runner requires Windows 10 or later.");

        string outputRoot = options.OutputDirectory ?? Path.Combine(
            repositoryRoot,
            ".hlslperf",
            "gpu-driven-demo",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputRoot);
        string[] levels = options.Level == "all" ? MeasuredLevels : [options.Level];

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            using D3D12Tuner tuner = new(options.Adapter);
            DeviceFingerprint device = tuner.DescribeDevice("6_6");
            Console.WriteLine($"Adapter: {device.AdapterName}");
            Console.WriteLine($"Driver:  {device.DriverVersion}");
            Console.WriteLine($"Output:  {outputRoot}");
            Console.WriteLine("Protocol: sequential candidates; complete application plan; poison/re-execute SHA-256 gate\n");

            List<CrowdLevelEvidence> evidence = [];
            foreach (string level in levels)
                evidence.Add(RunLevel(repositoryRoot, outputRoot, level, tuner, options.Resume, cancellation.Token));

            CrowdEvidenceArtifacts grid = CrowdEvidenceWriter.Write(outputRoot, device, evidence);
            CrowdLevelEvidence? visualWinner = evidence
                .Where(item => !item.RetainedBaseline && item.SelectedBackend == 2 && item.Speedup > 1)
                .OrderByDescending(item => item.Speedup)
                .FirstOrDefault();
            CrowdVisualArtifacts? visual = null;
            BudgetChoice? budget = null;
            if (visualWinner is not null)
            {
                budget = ChooseBudget(visualWinner, options.BudgetMilliseconds);
                if (budget is not null)
                {
                    byte[] atlas = File.ReadAllBytes(visualWinner.AtlasPath);
                    visual = CrowdVfxComposer.WriteBudgetCrossing(
                        device,
                        visualWinner,
                        budget,
                        atlas,
                        Path.Combine(outputRoot, "visual"));
                }
            }

            File.WriteAllText(
                Path.Combine(outputRoot, "gpu-driven-summary.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    createdUtc = DateTimeOffset.UtcNow,
                    device,
                    measurementProtocol = HlslPerfSdk.MeasurementProtocol,
                    levels = evidence,
                    bestVisualLevel = visualWinner?.Level,
                    budget,
                    visual
                }, JsonDefaults.Options),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine($"Pressure grid: {grid.CsvPath}");
            Console.WriteLine($"Heatmap:       {grid.SvgPath}");
            Console.WriteLine($"Best visual:   {visual?.GifPath ?? "no guarded budget crossing"}");
            return evidence.All(item => item.BaselineCorrect && item.SelectedCorrect) ? 0 : 2;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static CrowdLevelEvidence RunLevel(
        string repositoryRoot,
        string outputRoot,
        string level,
        D3D12Tuner tuner,
        bool resume,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"=== {level.ToUpperInvariant()} PRESSURE ===");
        string manifestPath = ManifestPath(repositoryRoot, level);
        TuningManifest manifest = TuningManifest.Load(manifestPath);
        CrowdVfxWorkload workload = new();
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        // Populate the immutable CPU oracle and scene metadata even when every
        // candidate is restored from a checkpoint and D3D12Tuner does not need
        // to call Build again.
        workload.Build(manifest, CandidateGenerator.ResolveBaseline(manifest, candidates));
        string levelDirectory = Path.Combine(outputRoot, level);
        string capturesDirectory = Path.Combine(levelDirectory, "content-addressed-captures");
        Directory.CreateDirectory(capturesDirectory);
        string checkpointPath = Path.Combine(levelDirectory, "checkpoint.json");
        if (resume && !File.Exists(checkpointPath))
            throw new FileNotFoundException($"Cannot resume {level}; checkpoint is missing.", checkpointPath);

        TuningRunReport report = tuner.Run(
            manifest,
            manifestPath,
            workload,
            PrintProgress,
            cancellationToken,
            Path.Combine(repositoryRoot, ".hlslperf", "cache", "dxil"),
            (_, output) =>
            {
                string hash = ContentHash.Sha256(output.Span);
                if (!string.Equals(hash, workload.Scene.ExpectedAtlasSha256, StringComparison.OrdinalIgnoreCase))
                    return;
                string path = Path.Combine(capturesDirectory, hash + ".rgba");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, output.Span);
            },
            new TuningCheckpointOptions(checkpointPath, resume));

        SelectionResult selection = report.Selection
            ?? throw new InvalidDataException($"No Crowd/VFX candidate passed the selection gates at {level} pressure.");
        CandidateResult baseline = report.Candidates.Single(item => item.CandidateId == report.BaselineCandidateId);
        CandidateResult selected = report.Candidates.Single(item => item.CandidateId == selection.CandidateId);
        if (baseline.Timing is null || selected.Timing is null ||
            baseline.Correctness?.Passed != true || selected.Correctness?.Passed != true)
            throw new InvalidDataException($"The {level} baseline or selected candidate did not pass timing and correctness.");

        ReportArtifacts artifacts = ReportWriter.Write(report, levelDirectory);
        string atlasPath = Path.Combine(capturesDirectory, workload.Scene.ExpectedAtlasSha256 + ".rgba");
        if (!File.Exists(atlasPath))
            throw new InvalidDataException(
                $"No actual GPU atlas survived for {level}. Resume from an output folder that retains content-addressed captures.");

        double speedup = baseline.Timing.MedianMilliseconds / selected.Timing.MedianMilliseconds;
        CrowdLevelEvidence result = new(
            level,
            workload.Scene.AgentCount,
            workload.Scene.FrameCount,
            workload.Scene.VisibleCounts.Min(),
            workload.Scene.VisibleCounts.Max(),
            workload.Scene.VisibleCounts.Average(),
            report.Candidates.Count,
            report.Candidates.Count(item => item.Correctness?.Passed == true),
            report.Candidates.Count(item => item.Stable),
            baseline.CandidateId,
            selected.CandidateId,
            Define(selected, "HLSLPERF_CROWD_BACKEND"),
            selection.RetainedBaseline,
            baseline.Correctness.Passed,
            selected.Correctness.Passed,
            baseline.Stable,
            selected.Stable,
            baseline.Timing.MedianMilliseconds,
            baseline.Timing.P95Milliseconds,
            baseline.Timing.CoefficientOfVariation,
            selected.Timing.MedianMilliseconds,
            selected.Timing.P95Milliseconds,
            selected.Timing.CoefficientOfVariation,
            speedup,
            workload.Scene.Width,
            workload.Scene.Height,
            workload.Scene.ExpectedAtlasSha256,
            baseline.SamplesMilliseconds,
            selected.SamplesMilliseconds,
            baseline.Defines,
            selected.Defines,
            artifacts.RunJsonPath,
            atlasPath);
        File.WriteAllText(
            Path.Combine(levelDirectory, "crowd-level-summary.json"),
            JsonSerializer.Serialize(result, JsonDefaults.Options),
            new UTF8Encoding(false));
        Console.WriteLine(
            $"{level}: {ShortCandidate(selected.Defines)} · {selected.Timing.MedianMilliseconds:0.0000} ms · " +
            $"{speedup:0.0000}× · atlas {workload.Scene.ExpectedAtlasSha256[..12]}…\n");
        return result;
    }

    private static BudgetChoice? ChooseBudget(CrowdLevelEvidence level, double requestedMilliseconds)
    {
        if (level.BaselineMedianMilliseconds > requestedMilliseconds &&
            level.SelectedP95Milliseconds <= requestedMilliseconds)
            return new BudgetChoice(requestedMilliseconds, "requested", 1000.0 / requestedMilliseconds);
        if (level.SelectedP95Milliseconds < level.BaselineMedianMilliseconds)
        {
            double measuredFit = (level.SelectedP95Milliseconds + level.BaselineMedianMilliseconds) / 2;
            return new BudgetChoice(measuredFit, "measured-fit", 1000.0 / measuredFit);
        }
        return null;
    }

    private static void PrintProgress(TuningProgress progress)
    {
        if (progress.Stage == "compile")
        {
            Console.Write($"[{progress.CandidateIndex,3}/{progress.CandidateCount}] {progress.CandidateId} ... ");
            return;
        }
        if (progress.Stage == "measure")
            return;
        CandidateResult result = progress.Result!;
        if (progress.Stage == "resumed")
        {
            Console.WriteLine($"resumed {result.Timing!.MedianMilliseconds:0.0000} ms");
            return;
        }
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

    private static RunOptions ParseRunOptions(string[] args)
    {
        string level = "all";
        string? output = null;
        string? adapter = null;
        bool resume = false;
        double budget = 1000.0 / 120.0;
        for (int index = 0; index < args.Length; ++index)
        {
            switch (args[index])
            {
                case "--level":
                    level = RequireValue(args, ref index, "--level").ToLowerInvariant();
                    if (level != "all" && !MeasuredLevels.Contains(level, StringComparer.Ordinal))
                        throw new ArgumentException("--level must be all, low, medium, high, or extreme.");
                    break;
                case "--output":
                    output = Path.GetFullPath(RequireValue(args, ref index, "--output"));
                    break;
                case "--adapter":
                    adapter = RequireValue(args, ref index, "--adapter");
                    break;
                case "--budget-ms":
                    if (!double.TryParse(
                            RequireValue(args, ref index, "--budget-ms"),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out budget) || !double.IsFinite(budget) || budget <= 0)
                        throw new ArgumentException("--budget-ms requires a positive finite number.");
                    break;
                case "--resume":
                    resume = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown run option '{args[index]}'.");
            }
        }
        if (resume && output is null)
            throw new ArgumentException("--resume requires the original --output directory.");
        return new RunOptions(level, output, adapter, budget, resume);
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
            GPU-driven Crowd/VFX application demo

              hlslperf-gpu-driven-demo validate
              hlslperf-gpu-driven-demo run [--level <all|low|medium|high|extreme>]
                                           [--output <directory>] [--adapter <name>]
                                           [--budget-ms <milliseconds>] [--resume]

            validate performs CPU oracle/plan checks and DXC shader compilation only.
            It never creates a D3D12 device. GPU work requires the explicit run command.
            """);
        return 0;
    }

    private static DxcShaderModel ParseShaderModel(string value) => value switch
    {
        "6_6" => DxcShaderModel.Model6_6,
        "6_7" => DxcShaderModel.Model6_7,
        _ => throw new InvalidDataException($"The Crowd/VFX validator does not support shader model '{value}'.")
    };

    private static int Define(CandidateResult candidate, string name) =>
        candidate.Defines.TryGetValue(name, out int value)
            ? value
            : throw new InvalidDataException($"Candidate '{candidate.CandidateId}' is missing define '{name}'.");

    internal static string ShortCandidate(IReadOnlyDictionary<string, int> defines)
    {
        int backend = defines["HLSLPERF_CROWD_BACKEND"];
        int group = defines["HLSLPERF_GROUP_SIZE"];
        int ept = defines["HLSLPERF_ELEMENTS_PER_THREAD"];
        if (backend == 1)
            return $"materialized · group={group} · EPT={ept}";
        int scale = defines.GetValueOrDefault("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 1);
        int wave = defines.GetValueOrDefault("HLSLPERF_WAVE_SIZE", 0);
        int replicas = defines.GetValueOrDefault("HLSLPERF_CROWD_TILE_REPLICAS", 1);
        return $"fused · wave{wave} · group={group} · IPT={ept * scale} · LDS×{replicas}";
    }

    private static string ManifestPath(string repositoryRoot, string level) => Path.Combine(
        repositoryRoot,
        "gpu-driven-demo",
        "manifests",
        $"crowd-vfx-{level}.json");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HlslKernelPipeline.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run the demo from inside the HlslKernelPipeline repository.");
    }
}

internal sealed record RunOptions(
    string Level,
    string? OutputDirectory,
    string? Adapter,
    double BudgetMilliseconds,
    bool Resume);

internal sealed record BudgetChoice(double Milliseconds, string Kind, double Hertz);

internal sealed record CrowdLevelEvidence(
    string Level,
    int AgentCount,
    int FrameCount,
    int MinimumVisibleCount,
    int MaximumVisibleCount,
    double MeanVisibleCount,
    int CandidateCount,
    int CorrectCandidateCount,
    int StableCandidateCount,
    string BaselineCandidateId,
    string SelectedCandidateId,
    int SelectedBackend,
    bool RetainedBaseline,
    bool BaselineCorrect,
    bool SelectedCorrect,
    bool BaselineStable,
    bool SelectedStable,
    double BaselineMedianMilliseconds,
    double BaselineP95Milliseconds,
    double BaselineCoefficientOfVariation,
    double SelectedMedianMilliseconds,
    double SelectedP95Milliseconds,
    double SelectedCoefficientOfVariation,
    double Speedup,
    int Width,
    int Height,
    string AtlasSha256,
    IReadOnlyList<double> BaselineSamplesMilliseconds,
    IReadOnlyList<double> SelectedSamplesMilliseconds,
    IReadOnlyDictionary<string, int> BaselineDefines,
    IReadOnlyDictionary<string, int> SelectedDefines,
    string RunJsonPath,
    string AtlasPath);
