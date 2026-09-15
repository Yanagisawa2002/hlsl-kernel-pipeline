using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.GpuDriven;
using HlslPerf.Workloads;

[assembly: SupportedOSPlatform("windows10.0")]

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonDefaults.Options) { WriteIndented = true };
    private static string Adapter => Environment.GetEnvironmentVariable("HLSLPERF_CROWD_ADAPTER") ?? "NVIDIA GeForce RTX 4090";
    private const int Requests = 12;
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3) throw new ArgumentException("oracle|debug-control|validate|check-scenes|run|rehearse <repository> <new-output> [reference-directory case arm]");
            string root = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
            if (Directory.Exists(output)) throw new IOException("Choose a new output directory; existing evidence is preserved.");
            Directory.CreateDirectory(output);
            var assembly = Assembly.GetExecutingAssembly();
            Save(Path.Combine(output, "process-identity.json"), new { pid = Environment.ProcessId,
                startedUtc = DateTimeOffset.UtcNow, command = args, assembly = assembly.Location,
                assemblySha256 = ContentHash.Sha256(File.ReadAllBytes(assembly.Location)),
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion });
            switch (args[0])
            {
                case "oracle": MakeReferences(root, output); break;
                case "debug-control": DebugControls(output); break;
                case "validate": Validate(root, output); break;
                case "check-scenes" when args.Length == 4: CheckScenes(root, output, Path.GetFullPath(args[3])); break;
                case "run" when args.Length == 6: Run(root, output, Path.GetFullPath(args[3]), args[4], args[5]); break;
                case "rehearse" when args.Length == 6: Run(root, output, Path.GetFullPath(args[3]), args[4], args[5], true); break;
                default: throw new ArgumentException("Unknown mode or arguments.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static byte[] Bytes(uint[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static uint[] Words(byte[] values) => MemoryMarshal.Cast<byte, uint>(values).ToArray();

    private static (KernelExecutionPlan Plan, byte[] Atlas) Reference(string root, CrowdApplicationScene s, int frames)
    {
        int bins = (int)BitOperations.RoundUpToPowerOf2((uint)(((s.Width + 15) / 16) * ((s.Height + 15) / 16)));
        TuningManifest manifest = new()
        {
            Name = "unchanged-crowd-cpu-oracle", KernelPath = Path.Combine(root, "gpu-driven-demo/crowd-vfx.hlsl"),
            ShaderModel = "6_6", Axes = [], Workload = new()
            {
                Id = "crowd-vfx-gpu-driven-v1", Parameters = new Dictionary<string, long>
                { ["agentCount"] = s.AgentCount, ["seed"] = s.Seed, ["width"] = s.Width, ["height"] = s.Height,
                  ["frameCount"] = frames, ["visibilityMask"] = s.VisibilityMask, ["tileSize"] = 16 }
            }
        };
        var candidate = new KernelCandidate(new Dictionary<string, int>
        {
            ["HLSLPERF_CROWD_BACKEND"] = 1, ["HLSLPERF_GROUP_SIZE"] = 256,
            ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4, ["HLSLPERF_VECTOR_WIDTH"] = 1,
            ["HLSLPERF_CROWD_TILE_SIZE"] = 16, ["HLSLPERF_CROWD_TILE_BINS"] = bins
        });
        CrowdVfxWorkload oracle = new();
        var plan = oracle.Build(manifest, candidate);
        return (plan, oracle.ExpectedAtlas.ToArray());
    }

    private static void MakeReferences(string root, string output)
    {
        List<object> manifest = [];
        // New confirmation seed and medium/dense tails were not in old tuning input.
        foreach (string cohort in new[] { "discovery", "confirmation" })
        {
            int seed = cohort == "discovery" ? 19088743 : 69501203;
            foreach (var (id, n, mask) in new (string, int, uint)[]
                { ("small", 262144, 15), ("medium-tail", 1048579, 63), ("large", 8388608, 511), ("dense-tail", 262147, 3) })
            {
                CrowdApplicationScene s = new(n, seed, mask);
                string key = cohort + "-" + id;
                long start = Stopwatch.GetTimestamp();
                var reference = Reference(root, s, Requests * s.Frames);
                byte[] input = CrowdApplication.GenerateAgents(n, seed);
                if (!input.AsSpan().SequenceEqual(reference.Plan.Buffers.Single(b => b.Name == "agents").InitialData))
                    throw new InvalidDataException("Runtime input differs from original generator.");
                int bytes = s.Width * s.Height * s.Frames * 4;
                List<string> hashes = [];
                for (int i = 0; i < Requests; i++) hashes.Add(ContentHash.Sha256(reference.Atlas.AsSpan(i * bytes, bytes)));
                File.WriteAllBytes(Path.Combine(output, key + ".rgba"), reference.Atlas);
                var row = new { id = key, scene = s, inputSha256 = ContentHash.Sha256(input), requestCount = Requests,
                    atlasBytes = bytes, hashes, fullAtlasSha256 = ContentHash.Sha256(reference.Atlas),
                    cpuOracleMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds };
                Save(Path.Combine(output, key + ".json"), row); manifest.Add(row);
                Save(Path.Combine(output, "references.json"), manifest);
                Console.WriteLine(key + " oracle complete");
            }
        }
    }

    private static byte[] ReadReference(string refs, string id, int request, int bytes)
    {
        byte[] data = new byte[bytes];
        using var stream = File.OpenRead(Path.Combine(refs, id + ".rgba"));
        stream.Position = (long)request * bytes; stream.ReadExactly(data); return data;
    }

    private static void Validate(string root, string output)
    {
        PrimitiveOperations.VerifyPinnedSource(root, "gps-reduce-then-scan");
        D3D12Tuner.EnableUnifiedDebugLayer();
        using var tuner = new D3D12Tuner(Adapter);
        var audit = new DebugEvidence(tuner, output);
        var executor = tuner.CreateUnifiedExecutor();
        List<object> checks = [];
        try
        {
            using (executor)
            {
                foreach (int n in new[] { 1, 31, 255, 1023, 4095, 4096, 4097, 8193, 65539 })
                foreach (uint mask in new[] { 0u, 15u, 0x7fffffffu })
                {
                    CrowdApplicationScene scene = new(n, 19088743, mask, 64, 32, 3);
                    var reference = Reference(root, scene, 6);
                    byte[] input = CrowdApplication.GenerateAgents(n, scene.Seed);
                    int bytes = scene.Width * scene.Height * scene.Frames * 4;
                    foreach (string arm in CrowdApplication.Arms)
                    {
                        var plan = CrowdApplication.Build(root, scene, input, arm, ContentHash.Sha256(reference.Atlas.AsSpan(0, bytes)));
                        // Two independently poisoned allocations, then reuse each through a new time interval.
                        foreach (byte poison in new byte[] { 0xa5, 0x5a })
                        {
                            var poisoned = plan with { Buffers = plan.Buffers.Select(b => b.Name == "agents" ? b :
                                b with { InitialData = Enumerable.Repeat(poison, b.ByteLength).ToArray() }).ToArray() };
                            using var session = executor.Prepare(poisoned);
                            using var reader = session.CreateCpuOutputReader("frame-atlas");
                            for (int window = 0; window < 2; window++)
                            {
                                reader.Execute(CrowdApplication.FrameConstants(plan, (uint)(window * scene.Frames)));
                                if (!reader.Data.Span.SequenceEqual(reference.Atlas.AsSpan(window * bytes, bytes)))
                                    throw new InvalidDataException($"Pixel mismatch {n}/{mask}/{arm}/{poison}/{window}");
                                CheckLists(session, scene, input, (uint)(window * scene.Frames + scene.Frames - 1), poison);
                                if (!session.ReadDiagnosticBuffer("agents").AsSpan().SequenceEqual(input)) throw new InvalidDataException("Input changed.");
                                audit.Check($"N={n}/mask={mask}/{arm}/poison={poison}/window={window}");
                                checks.Add(new { n, mask, arm, poison, window, fullPixelsPassed = true, stableListAndBinsPassed = true,
                                    inputUnchanged = true, atlasSha256 = ContentHash.Sha256(reader.Data.Span) });
                            }
                            // Caller input errors must fail before recording any GPU work.
                            try { reader.Execute(new Dictionary<string, uint[]> { ["missing"] = [1] }); throw new Exception("Missing override accepted."); }
                            catch (ArgumentException) { }
                        }
                    }
                    Save(Path.Combine(output, "correctness.json"), new { passed = false, complete = false, device = tuner.DescribeDevice("6_7"), count = checks.Count, checks });
                    Console.WriteLine($"validated N={n} mask={mask}; {checks.Count} full-output executions");
                }
            }
        }
        finally { audit.Capture("executor disposed; final queue drain"); }
        audit.Complete();
        Save(Path.Combine(output, "correctness.json"), new { passed = true, complete = true, device = tuner.DescribeDevice("6_7"), count = checks.Count, checks });
        Save(Path.Combine(output, "compilation.json"), executor.CompilationEvidence);
        Save(Path.Combine(output, "runtime.json"), RuntimeModules());
    }

    private static uint Hash(uint x) { x ^= x >> 16; x = unchecked(x * 0x7feb352d); x ^= x >> 15; x = unchecked(x * 0x846ca68b); return x ^ (x >> 16); }
    private static uint X(uint seed, uint f, uint width)
    { uint world = width * 4; return ((Hash(seed ^ 0x9e3779b9) % world + f * (1 + ((seed >> 3) & 3))) % world + world - f * 7 % world) % world; }
    private static uint Y(uint seed, uint f, uint height)
    { uint o = Hash(seed ^ 0x85ebca6b) % height, d = f * (1 + ((seed >> 7) & 1)); return (seed & 0x20) == 0 ? (o + d) % height : (o + height - d % height) % height; }

    private static void CheckLists(D3D12Tuner.UnifiedSession session, CrowdApplicationScene s, byte[] input, uint frame, byte? poison = null)
    {
        uint[] selected = Words(input).Where(seed => (Hash(seed ^ 0xd1b54a35) & s.VisibilityMask) == 0 && X(seed, frame, (uint)s.Width) < s.Width).ToArray();
        uint[] visible = Words(session.ReadDiagnosticBuffer("visible-seeds"));
        if (visible[0] != selected.Length || !visible.AsSpan(1, selected.Length).SequenceEqual(selected))
            throw new InvalidDataException("Stable visible sequence/count mismatch.");
        uint[] counts = Words(session.ReadDiagnosticBuffer("tile-counts"));
        uint[] offsets = Words(session.ReadDiagnosticBuffer("tile-offsets"));
        uint[] cursors = Words(session.ReadDiagnosticBuffer("tile-cursors"));
        uint[] seeds = Words(session.ReadDiagnosticBuffer("tile-seeds"));
        List<uint>[] expected = Enumerable.Range(0, counts.Length).Select(_ => new List<uint>()).ToArray();
        int tilesX = (s.Width + 15) / 16;
        foreach (uint seed in selected) expected[Y(seed, frame, (uint)s.Height) / 16 * (uint)tilesX + X(seed, frame, (uint)s.Width) / 16].Add(seed);
        uint total = 0;
        for (int tile = 0; tile < expected.Length; tile++)
        {
            if (counts[tile] != expected[tile].Count || cursors[tile] != expected[tile].Count || offsets[tile] != total)
                throw new InvalidDataException("Tile count/exclusive offset/cursor mismatch.");
            var actual = seeds.AsSpan((int)total, expected[tile].Count).ToArray(); Array.Sort(actual); expected[tile].Sort();
            if (!actual.SequenceEqual(expected[tile])) throw new InvalidDataException("Tile member/multiplicity mismatch.");
            total += counts[tile];
        }
        if (offsets[^1] != selected.Length || total != selected.Length) throw new InvalidDataException("Tile total mismatch.");
        if (poison is byte p)
        {
            uint sentinel = (uint)p * 0x01010101;
            // One extra word beyond the maximum valid list capacity.
            if (visible[^1] != sentinel || seeds[^1] != sentinel) throw new InvalidDataException("Output capacity guard overwritten.");
        }
    }

    private static void Run(string root, string output, string refs, string id, string arm, bool rehearsal = false)
    {
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(refs, id + ".json")));
        CrowdApplicationScene scene = reference.RootElement.GetProperty("scene").Deserialize<CrowdApplicationScene>(Json)!;
        var hashes = reference.RootElement.GetProperty("hashes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        int bytes = scene.Width * scene.Height * scene.Frames * 4;
        List<object> samples = [];
        var startedUtc = DateTimeOffset.UtcNow;
        long firstStart = Stopwatch.GetTimestamp(), stageStart = firstStart;
        byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
        double generation = Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;
        stageStart = Stopwatch.GetTimestamp();
        var plan = CrowdApplication.Build(root, scene, input, arm, hashes[0]);
        double planMs = Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;
        stageStart = Stopwatch.GetTimestamp();
        using var lifetime = new ResourceLifetime();
        var tuner = lifetime.Own(new D3D12Tuner(Adapter));
        var executor = lifetime.Own(tuner.CreateUnifiedExecutor());
        double deviceMs = Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;
        stageStart = Stopwatch.GetTimestamp();
        var session = lifetime.Own(executor.Prepare(plan, 512L * 1024 * 1024));
        var reader = lifetime.Own(session.CreateCpuOutputReader("frame-atlas"));
        double prepareMs = Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;
        double firstMs = 0;
        for (int i = 0; i < Requests; i++)
        {
            long requestStart = Stopwatch.GetTimestamp();
            var constants = CrowdApplication.FrameConstants(plan, (uint)(i * scene.Frames));
            var timing = reader.Execute(constants);
            long exportStart = Stopwatch.GetTimestamp();
            File.WriteAllBytes(Path.Combine(output, $"atlas-{i:D2}.rgba"), reader.Data.Span);
            double export = Stopwatch.GetElapsedTime(exportStart).TotalMilliseconds;
            double completed = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
            if (i == 0) firstMs = Stopwatch.GetElapsedTime(firstStart).TotalMilliseconds;
            string actualHash = ContentHash.Sha256(reader.Data.Span);
            byte[] expected = ReadReference(refs, id, i, bytes);
            bool passed = actualHash == hashes[i] && reader.Data.Span.SequenceEqual(expected);
            samples.Add(new { request = i, firstFrame = i * scene.Frames, phase = i == 0 ? "first-use" : i < 4 ? "warmup" : rehearsal ? "rehearsal" : "measured",
                completedMilliseconds = completed, exportMilliseconds = export, timing, actualHash, fullByteComparisonPassed = passed });
            Save(Path.Combine(output, "samples.json"), samples);
            if (!passed) throw new InvalidDataException($"Full atlas mismatch at {id}/{arm}/{i}.");
        }
        CheckLists(session, scene, input, (uint)(Requests * scene.Frames - 1));
        if (!session.ReadDiagnosticBuffer("agents").AsSpan().SequenceEqual(input)) throw new InvalidDataException("Input changed.");
        var device = tuner.DescribeDevice("6_7");
        if (device.AdapterName != Adapter) throw new InvalidDataException("Exact device not selected.");
        Save(Path.Combine(output, "compilation.json"), executor.CompilationEvidence);
        // Invasive per-pass markers run only after the performance samples, in discovery.
        if (!rehearsal && id.StartsWith("discovery-", StringComparison.Ordinal))
            Save(Path.Combine(output, "diagnostic.json"), session.MeasurePassDiagnostic(3));
        var memory = tuner.CaptureScenarioMemory();
        var runtime = RuntimeModules();
        lifetime.Dispose();
        Save(Path.Combine(output, "result.json"), new { schemaVersion = 1, passed = true, performanceEligible = !rehearsal, id, arm, scene,
            pid = Environment.ProcessId, startedUtc, endedUtc = DateTimeOffset.UtcNow, device,
            inputSha256 = ContentHash.Sha256(input), firstUseMilliseconds = firstMs, generationMilliseconds = generation,
            planMilliseconds = planMs, deviceMilliseconds = deviceMs, prepareMilliseconds = prepareMs,
            session.CpuPreparationMilliseconds, session.GpuUploadMilliseconds, session.UploadBytes,
            session.LogicalBytes, session.CommittedBytes, reader.CommittedReadbackBytes,
            hostInputAndOutputBytes = (long)input.Length + bytes, processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            memory, runtime, cleanupMilliseconds = lifetime.Milliseconds,
            fullOutputChecks = Requests, stableListAndBinsPassed = true, samples });
        Console.WriteLine($"completed {id}/{arm}; all {Requests} atlases and final stable list/bins exact");
    }

    private static void CheckScenes(string root, string output, string refs)
    {
        // Correctness only: ordinary desktop activity is allowed and no performance
        // samples or ratios are emitted by this mode.
        D3D12Tuner.EnableUnifiedDebugLayer();
        using var tuner = new D3D12Tuner(Adapter);
        var audit = new DebugEvidence(tuner, output);
        var executor = tuner.CreateUnifiedExecutor();
        List<object> checks = [];
        try
        {
            using (executor)
            {
                foreach (string file in Directory.GetFiles(refs, "*.json").Where(p =>
                    Path.GetFileName(p).StartsWith("discovery-", StringComparison.Ordinal) ||
                    Path.GetFileName(p).StartsWith("confirmation-", StringComparison.Ordinal)).Order())
                {
                    using var reference = JsonDocument.Parse(File.ReadAllText(file));
                    string id = reference.RootElement.GetProperty("id").GetString()!;
                    var scene = reference.RootElement.GetProperty("scene").Deserialize<CrowdApplicationScene>(Json)!;
                    string[] hashes = reference.RootElement.GetProperty("hashes").EnumerateArray().Select(p => p.GetString()!).ToArray();
                    byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
                    int bytes = scene.Width * scene.Height * scene.Frames * 4;
                    foreach (string arm in CrowdApplication.Arms)
                    {
                        var plan = CrowdApplication.Build(root, scene, input, arm, hashes[0]);
                        using var session = executor.Prepare(plan);
                        using var reader = session.CreateCpuOutputReader("frame-atlas");
                        foreach (int request in new[] { 0, Requests - 1 })
                        {
                            _ = reader.Execute(CrowdApplication.FrameConstants(plan, (uint)(request * scene.Frames)));
                            byte[] expected = ReadReference(refs, id, request, bytes);
                            if (!reader.Data.Span.SequenceEqual(expected) || ContentHash.Sha256(reader.Data.Span) != hashes[request])
                                throw new InvalidDataException($"Full scene mismatch {id}/{arm}/{request}");
                            CheckLists(session, scene, input, (uint)((request + 1) * scene.Frames - 1));
                            if (!session.ReadDiagnosticBuffer("agents").AsSpan().SequenceEqual(input)) throw new InvalidDataException("Scene input changed.");
                            audit.Check($"{id}/{arm}/request={request}");
                            string capture = Path.Combine(output, hashes[request] + ".rgba");
                            if (!File.Exists(capture)) File.WriteAllBytes(capture, reader.Data.Span);
                            checks.Add(new { id, arm, request, fullPixelsPassed = true, stableListAndBinsPassed = true,
                                atlasSha256 = hashes[request], session.LogicalBytes, session.CommittedBytes, reader.CommittedReadbackBytes });
                        }
                        Save(Path.Combine(output, "full-scenes.json"), new { passed = false, complete = false, count = checks.Count, device = tuner.DescribeDevice("6_7"), checks });
                        Console.WriteLine("full scene exact: " + id + "/" + arm);
                    }
                }
            }
        }
        finally { audit.Capture("executor disposed; final queue drain"); }
        audit.Complete();
        Save(Path.Combine(output, "full-scenes.json"), new { passed = true, complete = true, count = checks.Count, device = tuner.DescribeDevice("6_7"), checks });
        Save(Path.Combine(output, "compilation.json"), executor.CompilationEvidence);
        Save(Path.Combine(output, "runtime.json"), RuntimeModules());
    }

    private static void DebugControls(string output)
    {
        D3D12Tuner.EnableUnifiedDebugLayer();
        using var tuner = new D3D12Tuner(Adapter);
        var (initial, overflow, error) = tuner.RunUnifiedDebugQueueControls((context, snapshot) =>
            Save(Path.Combine(output, "debug-control-" + context + ".json"), snapshot));
        bool passed = initial.Passed && !overflow.Passed && overflow.DiscardedMessages > 0 &&
            overflow.StoredMessages == 2 && overflow.Messages.Length == 2 && !error.Passed && error.ErrorCount == 1;
        Save(Path.Combine(output, "debug-control.json"), new { passed, controlOnly = true,
            purpose = "Expected message loss and injected error must be rejected; this is not workload validation.", initial, overflow, error });
        if (!passed) throw new InvalidDataException("Debug queue rejection control failed.");
        Console.WriteLine("Debug queue overflow and error rejection controls passed.");
    }

    private sealed class DebugEvidence(D3D12Tuner tuner, string output)
    {
        private readonly List<object> snapshots = [];
        private bool allPassed = true;
        public void Capture(string context)
        {
            var snapshot = tuner.ReadUnifiedDebugSnapshot(clear: true);
            snapshots.Add(new { context, snapshot });
            allPassed &= snapshot.Passed;
            Save(Path.Combine(output, "debug.json"), new { schemaVersion = 2, passed = false, complete = false, allSnapshotsPassed = allPassed, snapshots });
        }
        public void Check(string context)
        {
            Capture(context);
            if (!allPassed) throw new InvalidDataException("Debug queue missing, truncated, filtered, changed during read, or contains an error; inspect debug.json.");
        }
        public void Complete()
        {
            if (!allPassed || snapshots.Count == 0) throw new InvalidDataException("Incomplete debug evidence; inspect debug.json.");
            Save(Path.Combine(output, "debug.json"), new { schemaVersion = 2, passed = true, complete = true, allSnapshotsPassed = true, snapshots });
        }
    }

    private static object[] RuntimeModules() => Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(m => m.ModuleName.Equals("dxcompiler.dll", StringComparison.OrdinalIgnoreCase) ||
                    m.ModuleName.Equals("dxil.dll", StringComparison.OrdinalIgnoreCase) ||
                    m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                    m.ModuleName.Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase))
        .OrderBy(m => m.ModuleName).Select(m => (object)new
        { name = m.ModuleName, path = m.FileName, version = m.FileVersionInfo.FileVersion, sha256 = ContentHash.Sha256(File.ReadAllBytes(m.FileName)) }).ToArray();

    private sealed class ResourceLifetime : IDisposable
    {
        private readonly Stack<IDisposable> resources = [];
        public double Milliseconds { get; private set; }
        public T Own<T>(T resource) where T : IDisposable { resources.Push(resource); return resource; }
        public void Dispose()
        {
            if (resources.Count == 0) return;
            long start = Stopwatch.GetTimestamp();
            while (resources.TryPop(out var resource)) resource.Dispose();
            Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }
}
