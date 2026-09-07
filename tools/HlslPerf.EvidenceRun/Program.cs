using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Rga;
using HlslPerf.Workloads;

return OperatingSystem.IsWindows() ? Run(args) : throw new PlatformNotSupportedException("Windows D3D12 required.");

[SupportedOSPlatform("windows10.0")]
static int Run(string[] args)
{
    if (args.Length != 4) throw new ArgumentException("Usage: EvidenceRun <repo> <matrix.json> <new-output-directory> <rga.exe>");
    string repo = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[2]);
    if (Directory.Exists(output)) throw new IOException("Use a new output directory to preserve all evidence attempts.");
    Directory.CreateDirectory(output);
    string matrixText = File.ReadAllText(args[1]);
    File.WriteAllText(Path.Combine(output, "declared-matrix.json"), matrixText);
    EvidenceMatrix matrix = JsonSerializer.Deserialize<EvidenceMatrix>(matrixText, JsonDefaults.Options)!;
    if (matrix.RunCount is < 1 or > 256 || matrix.Batches is < 3 or > 15 || matrix.Cells.Count is < 1 or > 12)
        throw new InvalidDataException("Evidence matrix exceeds bounded smoke limits.");
    List<object> results = [];
    List<RgaCandidateAnalysis> analyses = [];
    DateTimeOffset started = DateTimeOffset.UtcNow;
    using D3D12Tuner tuner = new("R9700");
    RgaInstallation installation = RgaLocator.Find(args[3]) ?? throw new FileNotFoundException("RGA unavailable", args[3]);
    RgaCollector rga = new(installation);
    HashSet<string> analyzed = [];
    bool passed = true;
    foreach (EvidenceCell cell in matrix.Cells)
    {
        string manifestPath = Path.Combine(repo, cell.Manifest);
        TuningManifest manifest = TuningManifest.Load(manifestPath);
        IKernelWorkload workload = BuiltinWorkloads.Resolve(manifest);
        KernelCandidate candidate = new(cell.Defines);
        WorkloadScenario scenario = new(cell.Id, cell.Seeds, cell.ElementCount, cell.MaximumAllocationBytes);
        Console.WriteLine($"Preparing {cell.Id} ({cell.ElementCount} items, {cell.Seeds.Count} slots)");
        try
        {
            using var session = tuner.PrepareScenario(manifest, manifestPath, workload, candidate, scenario);
            var before = session.VerifyAll();
            double warmup = session.MeasureBatch(matrix.RunCount);
            List<double> batches = [];
            for (int batch = 0; batch < matrix.Batches; batch++) batches.Add(session.MeasureBatch(matrix.RunCount));
            var after = session.VerifyAll();
            bool cellPassed = before.Concat(after).All(result => result.Correctness.Passed);
            passed &= cellPassed;
            results.Add(new { cell.Id, status = cellPassed ? "passed" : "failed", session.Evidence,
                verificationBefore = before, verificationAfter = after, warmupTotalGpuMilliseconds = warmup,
                matrix.RunCount, startSlot = 0, batchTotalGpuMilliseconds = batches,
                memoryAfterExecution = tuner.CaptureScenarioMemory() });
            Console.WriteLine($"{cell.Id}: correctness={cellPassed}, logical={session.Evidence.LogicalBufferBytes}, allocated={session.Evidence.CommittedAllocationBytes}");
            Save();
            if (cellPassed && analyzed.Add(cell.Manifest + candidate.Id))
            {
                string kernelPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.KernelPath));
                Console.WriteLine($"RGA {cell.Manifest}");
                var analysis = rga.AnalyzeCandidate(manifest, candidate, scenario.Build(manifest, workload, candidate)[0].Plan,
                    kernelPath, Path.Combine(output, "rga", cell.Id), "gfx1201", measuredDevice: tuner.DescribeDevice(manifest.ShaderModel));
                analyses.Add(analysis);
                passed &= analysis.Status == "collected";
                Save();
            }
        }
        catch (Exception exception)
        {
            passed = false;
            results.Add(new { cell.Id, status = "failed", exception = exception.ToString() });
            Console.WriteLine(exception);
            Save();
        }
    }
    Save();
    return passed ? 0 : 2;

    void Save()
    {
        string[] runtimeFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Concat(
            Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => module.ModuleName.Contains("dxcompiler", StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.Contains("dxil", StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.Equals("amdxc64.dll", StringComparison.OrdinalIgnoreCase))
                .Select(module => module.FileName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var report = new { schema = "hlslperf.bounded-hardware-evidence.v1", startedUtc = started,
            updatedUtc = DateTimeOffset.UtcNow, status = passed ? (results.Count == matrix.Cells.Count ? "passed" : "running") : "failed",
            purpose = "Bounded correctness and static resource evidence; smoke timestamps are not paired performance or deployment evidence.",
            matrixSha256 = ContentHash.Sha256(matrixText), device = tuner.DescribeDevice(),
            runtimeFiles = runtimeFiles.Select(path => new EvidenceFile(path, ContentHash.Sha256(File.ReadAllBytes(path)),
                new FileInfo(path).Length, FileVersionInfo.GetVersionInfo(path).FileVersion)).ToArray(),
            results, rga = analyses, temperatureCelsius = (double?)null, measuredOccupancy = (double?)null,
            measuredBandwidthBytesPerSecond = (double?)null,
            interference = "Shared mutex serializes cooperating validation only. External processes and clocks uncontrolled.",
            outstanding = "Integrated candidate comparisons require independent paired calibration/confirmation. Spill/occupancy/runtime counters unavailable unless explicitly present in raw artifacts." };
        File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(report, JsonDefaults.Options));
    }
}

sealed record EvidenceMatrix(int RunCount, int Batches, IReadOnlyList<EvidenceCell> Cells);
sealed record EvidenceCell(string Id, string Manifest, int ElementCount, IReadOnlyList<int> Seeds,
    long MaximumAllocationBytes, IReadOnlyDictionary<string,int> Defines);
