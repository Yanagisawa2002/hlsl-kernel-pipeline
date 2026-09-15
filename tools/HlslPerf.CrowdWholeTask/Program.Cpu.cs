using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.GpuDriven;

internal static partial class Program
{
    private static object CpuMachine() => new { processorCount = Environment.ProcessorCount,
        processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
        os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        runtime = RuntimeInformation.FrameworkDescription,
        jitEnvironment = new[] { "DOTNET_PROCESSOR_COUNT", "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun",
            "DOTNET_EnableHWIntrinsic", "DOTNET_gcServer", "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_ReadyToRun",
            "COMPlus_EnableHWIntrinsic", "COMPlus_gcServer" }.ToDictionary(name => name, Environment.GetEnvironmentVariable) };

    private static int[] CpuWorkers(CrowdApplicationScene scene) =>
        new[] { 1, 2, 4, 8, 12, CrowdCpuRenderer.MaximumWorkers(scene) }
            .Where(n => n <= CrowdCpuRenderer.MaximumWorkers(scene)).Distinct().Order().ToArray();

    private static void CheckCpuSequence(CrowdCpuRenderer renderer, CrowdApplicationScene scene, byte[] input, uint frame)
    {
        uint[] expected = Words(input).Where(seed => (Hash(seed ^ 0xd1b54a35) & scene.VisibilityMask) == 0 &&
            X(seed, frame, (uint)scene.Width) < scene.Width).ToArray();
        if (!renderer.VisibleSeeds(frame).SequenceEqual(expected)) throw new InvalidDataException("CPU stable visible sequence differs.");
    }

    private static void RunCpu(string output, string refs, string id, bool rehearsal, int? configuredWorkers)
    {
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(refs, id + ".json")));
        var scene = reference.RootElement.GetProperty("scene").Deserialize<CrowdApplicationScene>(Json)!;
        string[] hashes = reference.RootElement.GetProperty("hashes").EnumerateArray().Select(v => v.GetString()!).ToArray();
        string expectedInput = reference.RootElement.GetProperty("inputSha256").GetString()!;
        int workers = configuredWorkers ?? CrowdCpuRenderer.MaximumWorkers(scene);
        var startedUtc = DateTimeOffset.UtcNow;
        List<object> samples = [];
        long firstStart = Stopwatch.GetTimestamp(), stage = firstStart;
        byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
        double generation = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
        stage = Stopwatch.GetTimestamp();
        using var lifetime = new ResourceLifetime();
        var renderer = lifetime.Own(new CrowdCpuRenderer(scene, input, workers));
        double preparation = Stopwatch.GetElapsedTime(stage).TotalMilliseconds, firstMs = 0;
        for (int request = 0; request < Requests; request++)
        {
            long start = Stopwatch.GetTimestamp();
            var timing = renderer.Render((uint)(request * scene.Frames));
            long exportStart = Stopwatch.GetTimestamp();
            File.WriteAllBytes(Path.Combine(output, $"atlas-{request:D2}.rgba"), renderer.Data.Span);
            double export = Stopwatch.GetElapsedTime(exportStart).TotalMilliseconds;
            double completed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (request == 0) firstMs = Stopwatch.GetElapsedTime(firstStart).TotalMilliseconds;
            string actualHash = ContentHash.Sha256(renderer.Data.Span);
            bool passed = actualHash == hashes[request] && renderer.Data.Span.SequenceEqual(ReadReference(refs, id, request, renderer.Data.Length));
            samples.Add(new { request, firstFrame = request * scene.Frames,
                phase = request == 0 ? "first-use" : request < 4 ? "warmup" : rehearsal ? "rehearsal" : "measured",
                completedMilliseconds = completed, exportMilliseconds = export, cpuTiming = timing, actualHash, fullByteComparisonPassed = passed });
            Save(Path.Combine(output, "samples.json"), samples);
            if (!passed) throw new InvalidDataException($"CPU atlas differs: {id}/workers={workers}/request={request}");
        }
        CheckCpuSequence(renderer, scene, input, (uint)(Requests * scene.Frames - 1));
        if (ContentHash.Sha256(input) != expectedInput) throw new InvalidDataException("CPU input changed.");
        long logicalBytes = renderer.LogicalBytes;
        int eligibleCount = renderer.EligibleCount;
        var runtime = RuntimeModules();
        lifetime.Dispose();
        Save(Path.Combine(output, "compilation.json"), new { backend = "cpu", shaders = Array.Empty<object>(),
            assemblyIdentity = "process-identity.json", shaderCompilationApplicable = false });
        Save(Path.Combine(output, "result.json"), new { schemaVersion = 2, backend = "cpu", passed = true,
            performanceEligible = !rehearsal, id, arm = "cpu-frame-parallel", workers, scene, pid = Environment.ProcessId,
            startedUtc, endedUtc = DateTimeOffset.UtcNow, cpuMachine = CpuMachine(), inputSha256 = expectedInput,
            firstUseMilliseconds = firstMs, generationMilliseconds = generation, prepareMilliseconds = preparation,
            hostLogicalBytes = logicalBytes, allocationCapBytes = CrowdCpuRenderer.DefaultMemoryBudget, eligibleCount,
            processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, runtime,
            cleanupMilliseconds = lifetime.Milliseconds, cleanupContract = "Release managed references; normal CLR collection, no forced GC",
            fullOutputChecks = Requests, stableVisibleSequencePassed = true, inputUnchanged = true,
            tileBinsApplicable = false, gpuTimingApplicable = false, samples });
        Console.WriteLine($"CPU completed {id}/workers={workers}; all twelve atlases exact");
    }

    private static void CheckCpu(string root, string output, string refs)
    {
        List<object> checks = [];
        int boundaryCount = 0, fullCount = 0;
        foreach (int n in new[] { 1, 31, 255, 1023, 4095, 4096, 4097, 8193, 65539 })
        foreach (uint mask in new[] { 0u, 15u, 0x7fffffffu })
        {
            var scene = new CrowdApplicationScene(n, 19088743, mask, 64, 32, 3);
            var reference = Reference(root, scene, 6);
            byte[] input = CrowdApplication.GenerateAgents(n, scene.Seed);
            string inputHash = ContentHash.Sha256(input);
            int bytes = scene.Width * scene.Height * scene.Frames * 4;
            foreach (int workers in CpuWorkers(scene))
            {
                using var renderer = new CrowdCpuRenderer(scene, input, workers);
                foreach (byte poison in new byte[] { 0xa5, 0x5a })
                for (int window = 0; window < 2; window++)
                {
                    renderer.PoisonOutput(poison);
                    _ = renderer.Render((uint)(window * scene.Frames));
                    if (!renderer.Data.Span.SequenceEqual(reference.Atlas.AsSpan(window * bytes, bytes)))
                        throw new InvalidDataException("Boundary CPU pixels differ.");
                    CheckCpuSequence(renderer, scene, input, (uint)((window + 1) * scene.Frames - 1));
                    if (ContentHash.Sha256(input) != inputHash) throw new InvalidDataException("CPU input changed.");
                    checks.Add(new { kind = "boundary", n, mask, workers, poison, window, renderer.LogicalBytes,
                        fullPixelsPassed = true, stableVisibleSequencePassed = true, inputUnchanged = true }); boundaryCount++;
                }
            }
        }
        foreach (string file in Directory.GetFiles(refs, "*.json").Where(p => Path.GetFileName(p).StartsWith("discovery-", StringComparison.Ordinal) ||
            Path.GetFileName(p).StartsWith("confirmation-", StringComparison.Ordinal)).Order())
        {
            using var reference = JsonDocument.Parse(File.ReadAllText(file));
            string id = reference.RootElement.GetProperty("id").GetString()!;
            var scene = reference.RootElement.GetProperty("scene").Deserialize<CrowdApplicationScene>(Json)!;
            string inputHash = reference.RootElement.GetProperty("inputSha256").GetString()!;
            string[] hashes = reference.RootElement.GetProperty("hashes").EnumerateArray().Select(p => p.GetString()!).ToArray();
            byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
            foreach (int workers in CpuWorkers(scene))
            {
                using var renderer = new CrowdCpuRenderer(scene, input, workers);
                foreach (int request in new[] { 0, Requests - 1 })
                {
                    renderer.PoisonOutput(request == 0 ? (byte)0xa5 : (byte)0x5a);
                    var timing = renderer.Render((uint)(request * scene.Frames));
                    if (ContentHash.Sha256(renderer.Data.Span) != hashes[request] ||
                        !renderer.Data.Span.SequenceEqual(ReadReference(refs, id, request, renderer.Data.Length)))
                        throw new InvalidDataException("Full scene CPU pixels differ.");
                    CheckCpuSequence(renderer, scene, input, (uint)((request + 1) * scene.Frames - 1));
                    if (ContentHash.Sha256(input) != inputHash) throw new InvalidDataException("CPU scene input changed.");
                    string capture = Path.Combine(output, hashes[request] + ".rgba");
                    if (!File.Exists(capture)) File.WriteAllBytes(capture, renderer.Data.Span);
                    checks.Add(new { kind = "full-scene", id, workers, maximumWorkers = CrowdCpuRenderer.MaximumWorkers(scene), request,
                        renderer.EligibleCount, renderer.LogicalBytes, renderer.MemoryBudgetBytes,
                        timing.ParticipatingThreads, timing.PeakWorkers, atlasSha256 = hashes[request],
                        fullPixelsPassed = true, stableVisibleSequencePassed = true, inputUnchanged = true }); fullCount++;
                }
                Console.WriteLine($"CPU full scene exact: {id}/workers={workers}, logical bytes={renderer.LogicalBytes}");
            }
        }
        Save(Path.Combine(output, "cpu-check.json"), new { passed = true, complete = true, performanceEligible = false,
            boundaryCount, fullSceneCount = fullCount, count = checks.Count, cpuMachine = CpuMachine(),
            maximumWorkers = Math.Min(12, Environment.ProcessorCount), checks });
        Save(Path.Combine(output, "runtime.json"), RuntimeModules());
    }
}
