using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return 2;
string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string outputDirectory = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "artifacts/dynamic-smoke"));
Directory.CreateDirectory(outputDirectory);
// Frozen correctness matrix: zero, one, partial groups, diverse activity, clamped capacity,
// nonzero offsets, uint overflow count and the 65,535 group API ceiling.
(string Name, int Count, int Mode, int Cap, int Offset, int Group, int Overflow)[] cases =
[
    ("empty", 0, 0, 0, 0, 64, 0), ("zero-active", 129, 0, 129, 16, 64, 0),
    ("one", 1, 2, 1, 16, 64, 0), ("partial", 65, 2, 65, 0, 64, 0),
    ("predicate", 4096, 1, 4096, 16, 64, 0), ("capacity-clamp", 4096, 2, 127, 16, 64, 0),
    ("zero-capacity", 65, 2, 0, 16, 64, 0), ("uint-max-count", 129, 2, 129, 16, 64, 1),
    ("maximum-dispatch", 65_535, 2, 65_535, 16, 1, 0)
];
List<object> records = [];
bool passed = true;
using D3D12Tuner tuner = new("R9700");
foreach (var cell in cases)
{
    foreach (int seed in cell.Name == "predicate" ? new[] { 19, 71, 103 } : new[] { 19 })
    {
        KernelCandidate candidate = new(new Dictionary<string, int> { ["HLSLPERF_GROUP_SIZE"] = cell.Group });
        TuningManifest manifest = new()
        {
            SchemaVersion = "3.0", Name = cell.Name, KernelPath = "kernels/dynamic-compaction.hlsl",
            KernelAbiVersion = KernelAbiV2.Id, WorkItemCount = cell.Count,
            Axes = [new() { Name = "HLSLPERF_GROUP_SIZE", Values = [cell.Group] }],
            Workload = new()
            {
                Id = "dynamic-compaction-consume-u32-v2",
                Parameters = new Dictionary<string, long>
                {
                    ["elementCount"] = cell.Count, ["activeMode"] = cell.Mode, ["maximumItems"] = cell.Cap,
                    ["countByteOffset"] = cell.Offset, ["argumentByteOffset"] = cell.Offset,
                    ["seed"] = seed, ["overflowProbe"] = cell.Overflow
                }
            }
        };
        manifest.Validate();
        KernelExecutionPlan plan = new DynamicCompactionWorkload().Build(manifest, candidate);
        NativePlanValidation result = tuner.ValidateExecutionPlan(plan, Path.Combine(root, manifest.KernelPath), candidate.Defines);
        records.Add(new { cell.Name, seed, plan, result });
        passed &= result.Correctness.Passed;
        Console.WriteLine($"{cell.Name}/seed={seed}: {result.Correctness.Passed}, checks={result.Correctness.Outputs.Count}");
        if (cell.Name == "zero-active")
        {
            // Fresh/default zero memory must not hide an omitted output write on zero work.
            string brokenPath = Path.Combine(outputDirectory, "broken-reset.hlsl");
            File.WriteAllText(brokenPath, File.ReadAllText(Path.Combine(root, manifest.KernelPath))
                .Replace("if (tid.x < max(MaximumItems, 1)) Output0.Store(tid.x * 4, 0);", "// Deliberately omit consumer reset."));
            NativePlanValidation broken = tuner.ValidateExecutionPlan(plan, brokenPath, candidate.Defines, 1);
            bool rejected = !broken.Correctness.Passed && broken.Correctness.Outputs.Count(check =>
                check.Resource == "consumed" && !check.Passed) == 2;
            records.Add(new { name = "negative-missing-reset", rejected, result = broken });
            passed &= rejected;
        }
        if (cell.Name == "partial")
        {
            // A secondary-output corruption must reject the plan despite a correct primary hash.
            string brokenPath = Path.Combine(outputDirectory, "broken-bins.hlsl");
            File.WriteAllText(brokenPath, File.ReadAllText(Path.Combine(root, manifest.KernelPath))
                .Replace("(value & 7) * 4, 1, unused", "(value & 7) * 4, 2, unused"));
            NativePlanValidation broken = tuner.ValidateExecutionPlan(plan, brokenPath, candidate.Defines, 1);
            bool rejected = !broken.Correctness.Passed && broken.Correctness.Outputs.Where(check => !check.Passed)
                .All(check => check.Resource == "bins");
            records.Add(new { name = "negative-secondary-output", rejected, result = broken });
            passed &= rejected;
        }
    }
}
// Fixed-dispatch v1 still executes with its historical root layout and oracle.
TuningManifest legacy = JsonSerializer.Deserialize<TuningManifest>(File.ReadAllText(Path.Combine(root, "manifests/uint-mix-boundary.json")), JsonDefaults.Options)!;
KernelCandidate legacyCandidate = CandidateGenerator.Expand(legacy)[0];
KernelExecutionPlan legacyPlan = BuiltinWorkloads.Resolve(legacy).Build(legacy, legacyCandidate);
NativePlanValidation legacyResult = tuner.ValidateExecutionPlan(legacyPlan,
    Path.GetFullPath(Path.Combine(root, "manifests", legacy.KernelPath)), legacyCandidate.Defines);
records.Add(new { name = "legacy-v1", result = legacyResult });
passed &= legacyResult.Correctness.Passed;
var report = new
{
    schema = "dynamic-native-smoke-v1", utc = DateTimeOffset.UtcNow, passed,
    scope = KernelAbiV2.GpuScope, protocol = "correctness-only; diagnostic timestamps cannot select a candidate",
    binarySha256 = ContentHash.Sha256(File.ReadAllBytes(typeof(D3D12Tuner).Assembly.Location)),
    workloadBinarySha256 = ContentHash.Sha256(File.ReadAllBytes(typeof(DynamicCompactionWorkload).Assembly.Location)),
    records
};
string reportPath = Path.Combine(outputDirectory, "dynamic-native-smoke.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonDefaults.Options));
Console.WriteLine(reportPath);
return passed ? 0 : 1;
