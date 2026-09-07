using System.Reflection;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

[assembly: SupportedOSPlatform("windows10.0")]

if (args.Length != 2) throw new ArgumentException("Usage: RadixSmoke <repository-root> <evidence-directory>");
string root = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
// Predeclared correctness matrix. Timing is diagnostic smoke only, never selection evidence.
var cells = new (int Count, int Bits, int Pattern, int Domain)[]
{
    (0, 32, 2, 1), (1, 32, 5, 1), (127, 1, 2, 2), (128, 4, 3, 1),
    (129, 5, 2, 2), (257, 9, 5, 2), (4097, 31, 1, 2), (4097, 32, 4, 1),
    (262147, 32, 1, 1), (1048579, 32, 2, 1)
};
File.WriteAllText(Path.Combine(output, "matrix.json"), JsonSerializer.Serialize(new
{
    purpose = "compile-and-correctness-smoke-not-formal-comparison",
    cells = cells.Select(cell => new { cell.Count, cell.Bits, cell.Pattern, cell.Domain }),
    radixBits = new[] { 1, 4, 8 }, groupSize = 64, elementsPerThread = 2,
    candidateCoverage = new[] { "manifests/radix-wide.json", "manifests/radix-wide-pairs.json" },
    coverageCount = 1009, coverageBits = 9, coveragePattern = 2, coverageDomain = 2,
    compatibility = "positive 1009 records, 32 bits, extremes, ABI v1 keys and packed pairs, LDS scan",
    scope = "all GPU plan passes including histogram, scan, scatter, split and transitions",
    externalInterference = "Shared mutex held by runner; user apps and clocks uncontrolled."
}, JsonDefaults.Options));
using var tuner = new D3D12Tuner("R9700");
List<object> summaries = [];
bool passed = true;
foreach (bool pairs in new[] { false, true })
foreach (var cell in cells)
{
    string name = $"{(pairs ? "pairs" : "keys")}-{cell.Count}-bits{cell.Bits}-pattern{cell.Pattern}-domain{cell.Domain}";
    TuningManifest manifest = MakeManifest(name, cell.Count, cell.Bits, cell.Pattern, cell.Domain, pairs, KernelAbiV2.Id);
    RunCell(manifest, name, cell.Bits);
}
// Positive-count v1 packed compatibility and the retained LDS binary path.
foreach (bool pairs in new[] { false, true })
{
    string name = pairs ? "packed-v1-compatibility" : "keys-v1-compatibility";
    RunCell(MakeManifest(name, 1009, 32, 5, 1, pairs, KernelAbiV1.Id), name, 32);
}
// Compile and correctness-check the complete declared candidate set, including
// the historical vector binary path, both forced wave widths and 1024-item blocks.
foreach (string file in new[] { "radix-wide.json", "radix-wide-pairs.json" })
{
    JsonNode matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifests", file)))!;
    matrix["name"] = "candidate-coverage-" + Path.GetFileNameWithoutExtension(file);
    matrix["kernelPath"] = Path.Combine(root, "kernels", "radix-sort.hlsl");
    matrix["workItemCount"] = 1009;
    matrix["workload"]!["parameters"]!["elementCount"] = 1009;
    matrix["workload"]!["parameters"]!["bitCount"] = 9;
    matrix["workload"]!["parameters"]!["keyPattern"] = 2;
    matrix["workload"]!["parameters"]!["keyDomain"] = 2;
    matrix["warmupDispatches"] = 1;
    matrix["minimumWarmupMilliseconds"] = 0;
    matrix["measurementBatches"] = 3;
    matrix["dispatchesPerBatch"] = 1;
    matrix["maximumDispatchesPerBatch"] = 1;
    matrix["minimumBatchMilliseconds"] = 0.01;
    matrix["maximumCoefficientOfVariation"] = 1;
    RunCell(matrix.Deserialize<TuningManifest>(JsonDefaults.Options)!, matrix["name"]!.GetValue<string>(), 9);
}
File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
{
    passed, classification = "correctness-smoke-only", device = tuner.DescribeDevice("6_6"),
    binaries = new[] { typeof(D3D12Tuner).Assembly, typeof(RadixSortContract).Assembly, Assembly.GetExecutingAssembly() }
        .Select(assembly => new { path = assembly.Location, sha256 = ContentHash.Sha256(File.ReadAllBytes(assembly.Location)) }),
    compilerAndNativeBinaries = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path).Contains("dxc", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Contains("dxil", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal).Select(path => new { path, sha256 = ContentHash.Sha256(File.ReadAllBytes(path)) }),
    loadedCompilerModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(module => module.ModuleName.Equals("dxcompiler.dll", StringComparison.OrdinalIgnoreCase) ||
            module.ModuleName.Equals("dxil.dll", StringComparison.OrdinalIgnoreCase))
        .Select(module => new { path = module.FileName, version = module.FileVersionInfo.FileVersion,
            sha256 = ContentHash.Sha256(File.ReadAllBytes(module.FileName)) }),
    compiledShaders = (Directory.Exists(Path.Combine(output, "dxil"))
        ? Directory.EnumerateFiles(Path.Combine(output, "dxil"), "*.dxil") : [])
        .Order(StringComparer.Ordinal).Select(path => new { path, sha256 = ContentHash.Sha256(File.ReadAllBytes(path)) }),
    results = summaries
}, JsonDefaults.Options));
return passed ? 0 : 1;

void RunCell(TuningManifest manifest, string name, int bits)
{
    // Explicit even when running against a future integration whose default is paired.
    // Unknown fields are harmless on the historical backend used during development.
    JsonNode node = JsonSerializer.SerializeToNode(manifest, JsonDefaults.Options)!;
    node["measurementProtocol"] = "gpu-timestamps-poison-reexecute-v1";
    manifest = node.Deserialize<TuningManifest>(JsonDefaults.Options)!;
    string manifestPath = Path.Combine(output, name + ".manifest.json");
    File.WriteAllText(manifestPath, node.ToJsonString(JsonDefaults.Options));
    IKernelWorkload workload = BuiltinWorkloads.Resolve(manifest);
    TuningRunReport report = tuner.Run(manifest, manifestPath, workload,
        compilerCacheDirectory: Path.Combine(output, "dxil"));
    string runPath = Path.Combine(output, name + ".run.json");
    File.WriteAllText(runPath, JsonSerializer.Serialize(report, JsonDefaults.Options));
    foreach (CandidateResult result in report.Candidates)
    {
        KernelExecutionPlan plan = workload.Build(manifest, new(result.Defines));
        bool ok = result.Compiled && result.Correctness?.Passed == true;
        // V2 pairs must have both output hashes on both poison attempts, not only the primary key hash.
        if (manifest.KernelAbiVersion == KernelAbiV2.Id && manifest.Workload!.Id.Contains("pairs", StringComparison.Ordinal))
            ok &= result.Correctness?.Outputs.Count == 4 && result.Correctness.Outputs.All(value => value.Passed);
        passed &= ok;
        summaries.Add(new { name, result.CandidateId, passed = ok, result.Error,
            cost = RadixSortContract.Describe(plan, bits, result.Defines["HLSLPERF_RADIX_BITS"]),
            diagnosticCompletePlanMilliseconds = result.Timing?.MedianMilliseconds, raw = runPath });
        Console.WriteLine($"{name}: radix={result.Defines["HLSLPERF_RADIX_BITS"]} {(ok ? "PASS" : "FAIL")} {result.Error}");
    }
}

TuningManifest MakeManifest(string name, int count, int bits, int pattern, int domain, bool pairs, string abi) => new()
{
    SchemaVersion = "3.0", Name = name, KernelPath = Path.Combine(root, "kernels", "radix-sort.hlsl"),
    KernelAbiVersion = abi, ShaderModel = "6_6", WorkItemCount = Math.Max(1, count),
    WarmupDispatches = 1, MinimumWarmupMilliseconds = 0, MeasurementBatches = 3,
    DispatchesPerBatch = 1, MaximumDispatchesPerBatch = 1, MinimumBatchMilliseconds = 0.01,
    MaximumCoefficientOfVariation = 1,
    Workload = new() { Id = pairs ? "radix-sort-pairs-u32-v1" : "radix-sort-u32-v1",
        Parameters = new Dictionary<string, long> { ["elementCount"] = count, ["seed"] = 19088743,
            ["bitCount"] = bits, ["keyPattern"] = pattern, ["keyDomain"] = domain } },
    FixedDefines = new Dictionary<string, int> { ["HLSLPERF_RADIX_PAIRS"] = pairs ? 1 : 0,
        ["HLSLPERF_SCAN_OPERATOR"] = 1, ["HLSLPERF_GROUP_SIZE"] = 64,
        ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2, ["HLSLPERF_VECTOR_WIDTH"] = 1,
        ["HLSLPERF_SCAN_BACKEND"] = abi == KernelAbiV1.Id ? 1 : 2, ["HLSLPERF_WAVE_SIZE"] = 32 },
    Axes = [new() { Name = "HLSLPERF_RADIX_BITS", Values = [1, 4, 8] }],
    BaselineDefines = new Dictionary<string, int> { ["HLSLPERF_RADIX_BITS"] = 1 }
};
