using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

[assembly: SupportedOSPlatform("windows10.0")]
if (args.Length != 3) throw new ArgumentException("InclusiveScanValidation <repository> <exact-adapter> <new-output>");
string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("Preserve previous evidence; choose a new output directory.");
Directory.CreateDirectory(output);
using var tuner = new D3D12Tuner(args[1]);
var device = tuner.DescribeDevice("6_6");
if (device.AdapterName != args[1]) throw new InvalidDataException("The exact adapter was not selected.");
using var executor = tuner.CreateUnifiedExecutor();
var results = new List<object>();
bool allPassed = true;
int[] counts = [0, 1, 2, 3, 4, 5, 31, 32, 33, 127, 128, 129, 255, 256, 257,
    4095, 4096, 4097, 6145, 8193, 1048583, 4194311];
Save("declaration.json", new { counts, patterns = new[] { "full32-xorshift", "maximum-u32", "extremes" },
    repeats = 2, poisonsPerRepeat = 2, forcedFallback = "Start ticket at last partition with all predecessor statuses empty; compare entire output including untouched sentinels.",
    empty = "SDK zero guard plus actual zero-count kernel dispatch preserving three sentinels",
    oracle = "Independent uint64 accumulator reduced modulo 2^32; every output byte compared and hashed", device });
foreach (int n in counts)
foreach (int pattern in Enumerable.Range(0, 3))
{
    uint[] input = Values(n, pattern);
    foreach (bool inclusive in new[] { false, true })
    {
        var plan = inclusive ? PrimitiveOperations.InclusiveScan(root, input) :
            PrimitiveOperations.ExclusiveScan(root, input, ScanImplementation.WaveTiled);
        Run($"sdk-{(inclusive ? "inclusive" : "exclusive")}-{n}-{pattern}", plan, Oracle(input, inclusive));
    }
    Run($"gps-rts-exclusive-{n}-{pattern}", PrimitiveOperations.ExclusiveScan(root, input,
        ScanImplementation.GpuPrefixSumsReduceThenScan), Oracle(input, false));
}
// Compile/run the real compaction entry with its unchanged exclusive offsets.
foreach (int n in new[] { 1, 5, 4095, 4096, 4097, 8193, 1048583 })
foreach (uint mask in new[] { 0u, 7u, uint.MaxValue })
{
    uint[] input = Values(n, 0), selected = input.Where(v => (v & mask) == 0).ToArray();
    uint[] expected = [(uint)selected.Length, .. selected];
    var plan = PrimitiveOperations.ExclusiveScan(root, input, ScanImplementation.WaveTiled);
    plan = plan with
    {
        Implementation = "wave-tiled-compaction-check",
        Buffers = plan.Buffers.Select(b => b.Name == "output" ? b with { ByteLength = expected.Length * 4 } : b).ToArray(),
        Shaders = plan.Shaders.Select(s => s with { Id = "compaction/" + s.Id, SourcePath = Path.Combine(root, "kernels/compaction.hlsl"),
            EntryPoint = s.EntryPoint == WaveTiledScanCandidates.ScanEntryPoint ? WaveTiledScanCandidates.CompactionEntryPoint : s.EntryPoint }).ToArray(),
        Passes = plan.Passes.Select(p => p with { ShaderId = "compaction/" + p.ShaderId,
            Constants = p.Stage == UnifiedStage.Algorithm ? [(uint)n, 4096, (uint)((n + 4095) / 4096), mask] : p.Constants }).ToArray()
    };
    Run($"compaction-{n}-mask{mask}", plan, Bytes(expected));
}
{
    var emptyCompaction = PrimitiveOperations.ExclusiveScan(root, [1], ScanImplementation.WaveTiled);
    emptyCompaction = emptyCompaction with
    {
        LogicalCount = 0,
        Buffers = emptyCompaction.Buffers.Select(b => b.Name == "state" ? b with { ByteLength = 8 } : b).ToArray(),
        Shaders = emptyCompaction.Shaders.Select(s => s with { Id = "compaction/" + s.Id, SourcePath = Path.Combine(root, "kernels/compaction.hlsl"),
            EntryPoint = s.EntryPoint == WaveTiledScanCandidates.ScanEntryPoint ? WaveTiledScanCandidates.CompactionEntryPoint : s.EntryPoint }).ToArray(),
        Passes = [emptyCompaction.Passes[0] with { ShaderId = "compaction/" + emptyCompaction.Passes[0].ShaderId, Constants = [0, 0, 0] },
            emptyCompaction.Passes[1] with { ShaderId = "compaction/" + emptyCompaction.Passes[1].ShaderId, Constants = [0, 4096, 0, 7], Dispatch = new(1) }]
    };
    Run("kernel-empty-compaction", emptyCompaction, Bytes([0]));
}
foreach (bool inclusive in new[] { false, true })
{
    // A real scan dispatch with no owner for any predecessor deterministically
    // exercises the unchanged private fallback on the GPU, including overflow.
    uint[] input = Values(3 * 4096 + 3, 0);
    var plan = inclusive ? PrimitiveOperations.InclusiveScan(root, input) :
        PrimitiveOperations.ExclusiveScan(root, input, ScanImplementation.WaveTiled);
    uint[] seed = new uint[(8 + 12 * 4) / 4]; seed[1] = 3;
    uint[] sentinel = Enumerable.Repeat(0x735129abu, input.Length).ToArray();
    byte[] expected = Oracle(input, inclusive);
    Bytes(sentinel).AsSpan(0, 3 * 4096 * 4).CopyTo(expected);
    plan = plan with
    {
        Buffers = [.. plan.Buffers, new("seed", seed.Length * 4, Bytes(seed)), new("sentinel", sentinel.Length * 4, Bytes(sentinel))],
        ImmutableInputs = ["input", "seed", "sentinel"],
        Passes = [Copy("seed-state", "seed", "state", seed.Length * 4), Copy("seed-output", "sentinel", "output", sentinel.Length * 4),
            plan.Passes[1] with { Dispatch = new(1) }]
    };
    Run($"forced-fallback-inclusive{inclusive}", plan, expected);
    foreach (uint value in new[] { 0xa5a5a5a5u, 0x5a5a5a5au, 0x735129abu })
    {
        var empty = inclusive ? PrimitiveOperations.InclusiveScan(root, [1]) :
            PrimitiveOperations.ExclusiveScan(root, [1], ScanImplementation.WaveTiled);
        empty = empty with
        {
            LogicalCount = 0,
            Buffers = empty.Buffers.Select(b => b.Name == "output" ? b with { InitialData = Bytes([value]) } :
                b.Name == "state" ? b with { ByteLength = 8 } : b).ToArray(),
            ImmutableInputs = ["input", "output"],
            Passes = [empty.Passes[0] with { Constants = [0, 0, 0] }, empty.Passes[1] with { Constants = [0, 4096, 0], Dispatch = new(1) }]
        };
        Run($"kernel-empty-inclusive{inclusive}-sentinel{value}", empty, Bytes([value]));
    }
}
// One allocation reused across changing logical sizes and alternating modes.
// Capture every intermediate output so a later full scan cannot hide a failure.
{
    uint[] input = Values(5 * 4096 + 3, 0);
    var exclusive = PrimitiveOperations.ExclusiveScan(root, input, ScanImplementation.WaveTiled);
    var inclusive = PrimitiveOperations.InclusiveScan(root, input);
    var buffers = exclusive.Buffers.ToList();
    var passes = new List<UnifiedPass>();
    var expected = new Dictionary<string, byte[]>();
    int[] sizes = [input.Length, 1, 0, 2 * 4096 + 3, 3, input.Length];
    for (int i = 0; i < sizes.Length; ++i)
    {
        int count = sizes[i], blocks = (count + 4095) / 4096;
        bool isInclusive = i % 2 == 0;
        string capture = "capture-" + i;
        // The zero-count dispatch preserves the previous exclusive count-one zero.
        byte[] oracle = Oracle(input.AsSpan(0, count).ToArray(), isInclusive);
        expected.Add(capture, oracle);
        buffers.Add(new(capture, oracle.Length));
        passes.Add(exclusive.Passes[0] with { Name = "reset-" + i, Stage = UnifiedStage.Algorithm, Constants = [0, 0, (uint)blocks] });
        passes.Add(exclusive.Passes[1] with { Name = "scan-" + i, ShaderId = isInclusive ? inclusive.Passes[1].ShaderId : exclusive.Passes[1].ShaderId,
            Constants = [(uint)count, 4096, (uint)blocks], Dispatch = new((uint)Math.Max(1, blocks)) });
        passes.Add(Copy("capture-" + i, "output", capture, oracle.Length) with { Stage = UnifiedStage.Algorithm });
    }
    var reuse = exclusive with { Buffers = buffers, Passes = passes,
        Shaders = [.. exclusive.Shaders, inclusive.Shaders[1]],
        Outputs = expected.Select(p => new KernelVerifiedOutput(p.Key, ContentHash.Sha256(p.Value))).ToArray() };
    RunMany("shared-scratch-shrink-grow-alternating-modes", reuse, expected);
}
SaveResults();
Console.WriteLine($"PASS: {results.Count} GPU configurations; no performance selection.");
return allPassed ? 0 : 2;

void Save(string name, object data) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(data, JsonDefaults.Options));
void SaveResults() => Save("correctness.json", new { allPassed, device, results, executor.CompilationEvidence,
    memory = tuner.CaptureScenarioMemory(), processId = Environment.ProcessId,
    binaries = new[] { typeof(Program).Assembly, typeof(PrimitiveOperations).Assembly, typeof(D3D12Tuner).Assembly }
        .Select(a => new { a.Location, sha256 = ContentHash.Sha256(File.ReadAllBytes(a.Location)) }),
    compiler = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => m.ModuleName is "dxcompiler.dll" or "dxil.dll")
        .Select(m => new { path = m.FileName, sha256 = ContentHash.Sha256(File.ReadAllBytes(m.FileName)) }) });
void Run(string name, UnifiedOperationPlan plan, byte[] expected)
    => RunMany(name, plan, new Dictionary<string, byte[]> { [plan.Outputs[0].Resource] = expected });
void RunMany(string name, UnifiedOperationPlan plan, IReadOnlyDictionary<string, byte[]> expected)
{
    try
    {
        plan = plan with { Outputs = expected.Select(p => new KernelVerifiedOutput(p.Key, ContentHash.Sha256(p.Value))).ToArray() };
        plan.Validate();
        using var session = executor.Prepare(plan, 512L * 1024 * 1024);
        var checks = Enumerable.Range(0, 2).SelectMany(_ => session.Verify((resource, actual) =>
        {
            if (!actual.Span.SequenceEqual(expected[resource])) throw new InvalidDataException($"Full byte comparison failed: {name}/{resource}");
        })).ToArray();
        string inputName = plan.ImmutableInputs[0];
        bool inputUnchanged = session.ReadDiagnosticBuffer(inputName).SequenceEqual(plan.Buffers.Single(b => b.Name == inputName).InitialData!);
        bool passed = inputUnchanged && checks.All(c => c.Passed);
        if (!passed) throw new InvalidDataException("GPU output or immutable input mismatch.");
        results.Add(new { name, passed, inputUnchanged, planIdentity = OperationIdentity.Compute(plan, root), checks });
        Console.WriteLine(name + ": PASS");
    }
    catch (Exception error)
    {
        allPassed = false; results.Add(new { name, passed = false, error = error.ToString() }); SaveResults(); throw;
    }
    SaveResults();
}
static UnifiedPass Copy(string name, string source, string destination, int bytes) =>
    new(name, UnifiedStage.ScratchInitialization, null, null, [], [], []) { CopySource = source, CopyDestination = destination, CopyBytes = bytes };
static byte[] Bytes(uint[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
static byte[] Oracle(uint[] values, bool inclusive)
{
    uint[] expected = new uint[Math.Max(1, values.Length)]; ulong sum = 0;
    for (int i = 0; i < values.Length; ++i)
    {
        if (!inclusive) expected[i] = (uint)(sum & uint.MaxValue);
        sum += values[i];
        if (inclusive) expected[i] = (uint)(sum & uint.MaxValue);
    }
    return Bytes(expected);
}
static uint[] Values(int count, int pattern)
{
    uint state = 0x917923;
    return Enumerable.Range(0, count).Select(i =>
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return pattern == 1 ? uint.MaxValue : pattern == 2 ? (i % 4) switch { 0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u, _ => 1u } : state;
    }).ToArray();
}
