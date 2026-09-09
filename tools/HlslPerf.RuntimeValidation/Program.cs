using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

[assembly: SupportedOSPlatform("windows10.0")]
if (args.Length != 3 || args[0] is not ("probe" or "validate" or "validate-empty"))
    throw new ArgumentException("RuntimeValidation probe|validate|validate-empty <repository> <new-output>");
string root = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("Preserve earlier evidence; use a new output directory.");
Directory.CreateDirectory(output);
using var tuner = new D3D12Tuner("R9700");
var device = tuner.DescribeDevice("6_6");
var before = tuner.CaptureScenarioMemory();
Save("device.json", new { device, memory = before, processId = Environment.ProcessId,
    binaries = new[] { typeof(D3D12Tuner).Assembly, typeof(PrimitiveOperations).Assembly, typeof(Program).Assembly }
        .Select(a => new { a.Location, sha256 = ContentHash.Sha256(File.ReadAllBytes(a.Location)) }) });
Console.WriteLine(JsonSerializer.Serialize(new { device, memory = before }, JsonDefaults.Options));
if (args[0] == "probe") return 0;
using var executor = tuner.CreateUnifiedExecutor();
if (args[0] == "validate-empty")
{
    var emptyChecks = new List<object>();
    foreach (uint sentinel in new[] { 0xa5a5a5a5u, 0x5a5a5a5au, 0x735129abu })
    {
        byte[] bytes = Bytes([sentinel]);
        UnifiedOperationPlan plan = new("host-empty-no-dispatch", 0, "preserve-output-sentinel", ContentHash.Sha256(bytes),
            [new("input", 4, bytes), new("output", 4, bytes)], [],
            [new("unused-copy", UnifiedStage.InputRestore, null, null, [], [], []) { CopySource = "input", CopyDestination = "output", CopyBytes = 4 }],
            [new("output", ContentHash.Sha256(bytes))]) { ImmutableInputs = ["input", "output"] };
        using var session = executor.Prepare(plan);
        for (int repeat = 0; repeat < 4; ++repeat)
        {
            // Count zero returns from the host operation: no plan execution,
            // no reset, no dispatch, and no sentinel-restoring copy.
            byte[] actual = session.ReadDiagnosticBuffer("output");
            bool passed = actual.SequenceEqual(bytes);
            emptyChecks.Add(new { sentinel, repeat, passed, sha256 = ContentHash.Sha256(actual), dispatches = 0 });
            if (!passed) throw new InvalidDataException("Empty host operation changed the sentinel.");
        }
    }
    Save("empty-correctness.json", new { schema = "hlslperf.empty-no-dispatch.v1", passed = true, device, emptyChecks });
    Console.WriteLine("Empty scan: 12 GPU readback checks passed; zero dispatches and zero restoration copies.");
    return 0;
}
var results = new List<object>();
bool allPassed = true;
int[] scanCounts = [0, 1, 3, 31, 32, 33, 255, 256, 257, 4095, 4096, 4097, 8193, 1048583, 4194311];
int[] sortCounts = [0, 1, 31, 32, 33, 127, 128, 129, 255, 256, 257, 511, 512, 513, 4097, 262145, 1048583];
Save("declaration.json", new { purpose = "GPU correctness only, no benchmark selection", scanCounts, sortCounts,
    scanPatterns = new[] { "full32-random", "maximum-u32", "extremes" },
    sortPatterns = new[] { "full32-random", "duplicate-full32", "reverse", "extremes" },
    radixBits = new[] { 4, 8 }, repeatVerifyCalls = 2, poisonAttemptsPerCall = 2,
    inputs = "fixed xorshift32 seed 0x917923; independent stable comparison-sort payload oracle" });
foreach (int n in scanCounts)
foreach (int pattern in Enumerable.Range(0, 3))
{
    uint[] input = Values(n, pattern);
    Run($"scan-wave32-{n}-{pattern}", Scan(input));
    Run($"scan-gps-rts-{n}-{pattern}", PrimitiveOperations.ExclusiveScan(root, input, ScanImplementation.GpuPrefixSumsReduceThenScan));
}
foreach (int n in sortCounts)
foreach (int pattern in Enumerable.Range(0, 4))
foreach (bool pairs in new[] { false, true })
{
    uint[] keys = Values(n, pattern == 1 ? 3 : pattern == 2 ? 4 : pattern == 3 ? 2 : 0);
    uint state = 0x781239;
    uint[]? payload = pairs ? Enumerable.Range(0, n).Select(_ => Next(ref state)).ToArray() : null;
    foreach (int radix in new[] { 4, 8 }) Run($"radix-{radix}-{n}-{pattern}-pairs{pairs}", Sort(keys, payload, radix));
    Run($"radix-amd-{n}-{pattern}-pairs{pairs}", PrimitiveOperations.StableSort(root, keys, payload, SortImplementation.AmdParallelSort));
}
SaveResults();
return allPassed ? 0 : 2;

void Save(string file, object value) => File.WriteAllText(Path.Combine(output, file), JsonSerializer.Serialize(value, JsonDefaults.Options));
void SaveResults() => Save("correctness.json", new { schema = "hlslperf.runtime-correctness.v1", allPassed,
    device, memoryBefore = before, memoryAfter = tuner.CaptureScenarioMemory(), results, executor.CompilationEvidence,
    compiler = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => m.ModuleName is "dxcompiler.dll" or "dxil.dll")
        .Select(m => new { path = m.FileName, sha256 = ContentHash.Sha256(File.ReadAllBytes(m.FileName)) }) });
void Run(string name, UnifiedOperationPlan plan)
{
    try
    {
        using var session = executor.Prepare(plan, 512L * 1024 * 1024);
        var checks = Enumerable.Range(0, 2).SelectMany(_ => session.Verify()).ToArray();
        bool passed = checks.All(c => c.Passed);
        allPassed &= passed;
        results.Add(new { name, passed, planIdentity = OperationIdentity.Compute(plan, root), checks,
            session.CommittedBytes, session.LogicalBytes });
        Console.WriteLine($"{name}: {(passed ? "PASS" : "FAIL")}");
        if (!passed) throw new InvalidDataException("GPU output differs from independent oracle.");
    }
    catch (Exception error)
    {
        allPassed = false;
        results.Add(new { name, passed = false, error = error.ToString() });
        Console.WriteLine(error);
        SaveResults();
        throw; // fail closed before any timing process can be launched
    }
    SaveResults();
}
UnifiedOperationPlan Scan(uint[] input)
{
    int n = input.Length;
    byte[] data = Bytes(n == 0 ? [0x735129abu] : input);
    byte[] expected = new byte[Math.Max(1, n) * 4];
    uint sum = 0;
    for (int i = 0; i < n; i++) { BitConverter.TryWriteBytes(expected.AsSpan(i * 4), sum); sum = unchecked(sum + input[i]); }
    if (n == 0) expected = data.ToArray();
    int blocks = (n + 4095) / 4096;
    var defines = WaveTiledScanCandidates.Create().Defines.ToDictionary(p => p.Key, p => p.Value.ToString());
    UnifiedShader[] shaders = n == 0 ? [] : new[] { "ResetWaveTiledState", "SinglePassScanWaveTiled" }
        .Select(entry => new UnifiedShader(entry, Path.Combine(root, "kernels/scan.hlsl"), entry, defines, [Path.Combine(root, "kernels")], [])).ToArray();
    UnifiedPass[] passes = n == 0 ? [new("empty-host-sentinel", UnifiedStage.InputRestore, null, null, [], [], [])
        { CopySource = "input", CopyDestination = "output", CopyBytes = 4 }] :
        [new("reset", UnifiedStage.ScratchInitialization, "ResetWaveTiledState", new(1), [], ["state"], [0, 0, (uint)blocks]),
         new("scan", UnifiedStage.Algorithm, "SinglePassScanWaveTiled", new((uint)Math.Min(blocks, 256)), ["input"], ["output", "state"], [(uint)n, 4096, (uint)blocks])];
    return new("wave-tiled", n, "exclusive-u32-sum-modulo-2^32", ContentHash.Sha256(data),
        [new("input", data.Length, data), new("output", expected.Length), new("state", Math.Max(8, 8 + 12 * blocks))], shaders, passes,
        [new("output", ContentHash.Sha256(expected))]) { ImmutableInputs = ["input"] };
}
UnifiedOperationPlan Sort(uint[] keys, uint[]? payload, int radix)
{
    bool pairs = payload is not null;
    int n = keys.Length;
    var layout = RadixTileLayout.Create(n, 32, radix, pairs);
    var manifest = new TuningManifest { SchemaVersion = "3.0", Name = "runtime-correctness", KernelPath = "kernels/radix-sort.hlsl",
        ShaderModel = "6_6", KernelAbiVersion = KernelAbiV2.Id, WorkItemCount = Math.Max(1, n), Axes = [],
        Workload = new() { Id = pairs ? "radix-sort-pairs-u32-v1" : "radix-sort-u32-v1", Parameters = new Dictionary<string, long> { ["elementCount"] = n, ["bitCount"] = 32 } } };
    var candidate = new KernelCandidate(layout.Defines);
    var original = BuiltinWorkloads.Resolve(manifest).Build(manifest, candidate);
    byte[] data = pairs ? RadixSortContract.Pack(keys, payload) : Bytes(n == 0 ? [0u] : keys);
    int[] order = Enumerable.Range(0, n).OrderBy(i => keys[i]).ToArray();
    byte[] sortedKeys = Bytes(n == 0 ? [0u] : order.Select(i => keys[i]).ToArray());
    byte[]? sortedPayload = pairs ? Bytes(n == 0 ? [0u] : order.Select(i => payload![i]).ToArray()) : null;
    var buffers = original.Buffers.Select(b => b.InitialData is not null ? b with { InitialData = data } : b).ToArray();
    string prefix = $"tile{radix}-pairs{pairs}-";
    var defines = candidate.Defines.ToDictionary(p => p.Key, p => p.Value.ToString());
    var shaders = original.Passes.Select(p => p.EntryPoint).Distinct().Select(entry => new UnifiedShader(prefix + entry,
        Path.Combine(root, "kernels/radix-sort.hlsl"), entry, defines, [Path.Combine(root, "kernels")], [])).ToArray();
    var passes = original.Passes.Select(p => new UnifiedPass(p.Name, UnifiedStage.Algorithm, prefix + p.EntryPoint,
        p.Dispatch, [p.Input0, p.Input1], [p.Output0, p.Output1], p.Constants)).ToArray();
    var verified = original.GetVerifiedOutputs().ToArray();
    var outputs = verified.Select((o, i) => new KernelVerifiedOutput(o.Resource, ContentHash.Sha256(i == 0 ? sortedKeys : sortedPayload!))).ToArray();
    return new(prefix, n, "stable-full32", ContentHash.Sha256(data), buffers, shaders, passes, outputs)
        { ImmutableInputs = buffers.Where(b => b.InitialData is not null).Select(b => b.Name).ToArray() };
}
static byte[] Bytes(uint[] values) { byte[] bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
static uint Next(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
static uint[] Values(int n, int pattern)
{
    uint s = 0x917923;
    return Enumerable.Range(0, n).Select(i => pattern switch { 1 => uint.MaxValue, 2 => (i % 4) switch { 0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u, _ => 1u },
        3 => (Next(ref s) % 7) * 0x24924924u, 4 => (uint)(n - i), _ => Next(ref s) }).ToArray();
}
