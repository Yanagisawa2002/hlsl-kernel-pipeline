using System.Diagnostics;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

internal static class UnifiedBenchRunner
{
    public static int Run(string[] args)
    {
        string repo = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output)) throw new InvalidDataException("Refusing to overwrite an experiment.");
        Directory.CreateDirectory(output);
        return args[0] == "correctness" ? Correctness(repo, output) : Formal(repo, output, args[3], args[4], int.Parse(args[5]), args[0] == "pilot");
    }

    private static object RuntimeIdentity() => new
    {
        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        binaries = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".dll" or ".exe" or ".json")
            .Order(StringComparer.Ordinal).Select(path => new { path, sha256 = ContentHash.Sha256(File.ReadAllBytes(path)),
                fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion }).ToArray()
    };

    private static int Correctness(string repo, string output)
    {
        using D3D12Tuner tuner = new("R9700");
        using var executor = tuner.CreateUnifiedExecutor();
        List<object> results = []; bool passed = true;
        var cases = new List<(string Workload, int Count, string Pattern, bool Pairs)>();
        int[] boundaries = [0, 1, 3, 4, 127, 128, 255, 256, 511, 512, 513, 1023, 1024, 1025, 3071, 3072, 3073, 6145, 409599, 409600, 409601];
        foreach (int count in boundaries)
        {
            cases.Add(("scan", count, "uniform", false));
            cases.Add(("radix", count, "uniform", false));
            cases.Add(("radix", count, "uniform", true));
        }
        foreach (string pattern in new[] { "ones", "zeros", "extremes" }) cases.Add(("scan", 6145, pattern, false));
        foreach (string pattern in new[] { "duplicate", "equal", "descending", "extremes" })
            foreach (bool pairs in new[] { false, true }) cases.Add(("radix", 6145, pattern, pairs));
        DateTimeOffset started = DateTimeOffset.UtcNow;
        object runtime = RuntimeIdentity();
        foreach (var item in cases)
        {
            var fixture = UnifiedWorkloads.Fixture(item.Workload, item.Count, 991027, item.Pattern, item.Pairs);
            foreach (string arm in item.Workload == "scan" ? UnifiedWorkloads.ScanImplementations : UnifiedWorkloads.RadixImplementations)
            {
                try
                {
                    var plan = UnifiedWorkloads.Build(repo, fixture, arm);
                    using var session = executor.Prepare(plan);
                    var verification = session.Verify();
                    bool valid = verification.All(result => result.Passed); passed &= valid;
                    results.Add(new { fixture, implementation = arm, passed = valid, verification });
                    Console.WriteLine($"{item.Workload}/{item.Count}/{item.Pattern}/{item.Pairs}/{arm}: {valid}");
                }
                catch (Exception error)
                {
                    passed = false; results.Add(new { fixture, implementation = arm, passed = false, error = error.ToString() });
                    Save();
                    // A native/runtime error invalidates this process; do not cascade into later arms.
                    return 3;
                }
                Save();
            }
        }
        Save(); return passed ? 0 : 2;
        void Save() => Write(output, "correctness.json", new { schema = "hlslperf.unified-correctness.v1", developmentOnly = true,
            pid = Environment.ProcessId, startedUtc = started, recordedUtc = DateTimeOffset.UtcNow, testsPassed = passed,
            device = tuner.DescribeDevice("per-arm:6_6-or-6_7"), deviceRemovalStatus = tuner.DeviceRemovalStatus, runtime,
            compilation = executor.CompilationEvidence, results });
    }

    private static int Formal(string repo, string output, string declarationPath, string cellId, int processIndex, bool developmentOnly)
    {
        byte[] declarationBytes = File.ReadAllBytes(declarationPath);
        var declaration = JsonSerializer.Deserialize<Declaration>(declarationBytes, JsonDefaults.Options) ?? throw new InvalidDataException("Missing declaration.");
        if (declaration.Schema != "hlslperf.unified-declaration.v1" || processIndex is < 1 or > 5 || declaration.Repetitions != 18 || declaration.Blocks != 12)
            throw new InvalidDataException("Invalid frozen protocol.");
        Cell cell = declaration.Cells.Single(c => c.Id == cellId);
        Schedule schedule = cell.Processes.Single(p => p.Index == processIndex);
        if (schedule.Seeds.Length != cell.Slots || schedule.Orders.Length != declaration.Blocks ||
            schedule.Orders.Any(order => !order.Order().SequenceEqual(cell.Arms.Order()))) throw new InvalidDataException("Incomplete schedule.");
        using D3D12Tuner tuner = new("R9700");
        using var executor = tuner.CreateUnifiedExecutor();
        var rings = new Dictionary<string, List<D3D12Tuner.UnifiedSession>>();
        var statuses = cell.Arms.ToDictionary(arm => arm, _ => "pending");
        List<object> setup = [], checks = [], warmup = [], observations = [], errors = [];
        long fixtureStart = Stopwatch.GetTimestamp();
        var fixtures = schedule.Seeds.Select(seed => UnifiedWorkloads.Fixture(cell.Workload, cell.Count, seed, cell.Pattern, cell.Pairs)).ToArray();
        double fixtureMilliseconds = Stopwatch.GetElapsedTime(fixtureStart).TotalMilliseconds;
        object runtime = RuntimeIdentity(); DateTimeOffset started = DateTimeOffset.UtcNow;
        bool completed = false;
        try
        {
            Save();
            // Every arm has its own complete correctness gate, regardless of any other arm's result.
            foreach (string arm in schedule.Orders[0])
            {
                rings[arm] = [];
                bool valid = true;
                for (int slot = 0; slot < cell.Slots; slot++)
                {
                    long start = Stopwatch.GetTimestamp();
                    var plan = UnifiedWorkloads.Build(repo, fixtures[slot], arm);
                    double planMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var session = executor.Prepare(plan); rings[arm].Add(session);
                    setup.Add(new { arm, slot, planMilliseconds, session.CpuPreparationMilliseconds, session.GpuUploadMilliseconds,
                        session.UploadBytes, session.CommittedBytes, session.LogicalBytes, passes = plan.Passes,
                        buffers = plan.Buffers.Select(buffer => new { buffer.Name, buffer.ByteLength, initialized = buffer.InitialData is not null }) });
                    var verification = session.Verify(); valid &= verification.All(result => result.Passed);
                    checks.Add(new { phase = "before", arm, slot, verification }); Save();
                }
                statuses[arm] = valid ? "correct" : "correctness_failed";
                Console.WriteLine($"{cell.Id}/p{processIndex}/{arm}: {statuses[arm]}"); Save();
            }
            for (int batch = 0; batch < declaration.WarmupBatches; batch++)
                foreach (string arm in schedule.Orders[batch % declaration.Blocks])
                    if (statuses[arm] == "correct")
                        warmup.Add(new { arm, batch, timing = D3D12Tuner.UnifiedSession.MeasureRingBatch(rings[arm], declaration.WarmupRepetitions) });
            Save();
            for (int block = 0; block < declaration.Blocks; block++)
            {
                for (int half = 0; half < 2; half++)
                {
                    string[] order = half == 0 ? schedule.Orders[block] : schedule.Orders[block].Reverse().ToArray();
                    for (int position = 0; position < order.Length; position++)
                    {
                        string arm = order[position];
                        if (statuses[arm] != "correct")
                            observations.Add(new { block, half, position, arm, status = "not_timed_correctness_failed" });
                        else
                            observations.Add(new { block, half, position, arm, status = "measured",
                                timing = D3D12Tuner.UnifiedSession.MeasureRingBatch(rings[arm], declaration.Repetitions) });
                    }
                }
                Save(); Console.WriteLine($"{cell.Id}/p{processIndex}: block {block + 1}/{declaration.Blocks}");
            }
            foreach (string arm in cell.Arms.Where(arm => statuses[arm] == "correct"))
            {
                bool valid = true;
                for (int slot = 0; slot < cell.Slots; slot++)
                {
                    var verification = rings[arm][slot].Verify(); valid &= verification.All(result => result.Passed);
                    checks.Add(new { phase = "after", arm, slot, verification });
                }
                statuses[arm] = valid ? "measured_correct" : "post_correctness_failed";
            }
            completed = true; Save();
            return statuses.Values.All(value => value == "measured_correct") ? 0 : 2;
        }
        catch (Exception error)
        {
            errors.Add(new { error = error.ToString(), deviceRemovalStatus = tuner.DeviceRemovalStatus }); Save(); return 3;
        }
        finally { foreach (var ring in rings.Values) foreach (var session in ring) session.Dispose(); }
        void Save() => Write(output, "process.json", new
        {
            schema = "hlslperf.unified-process.v1", developmentOnly, completed, pid = Environment.ProcessId,
            processIndex, startedUtc = started, recordedUtc = DateTimeOffset.UtcNow,
            declarationSha256 = ContentHash.Sha256(declarationBytes), cell, schedule, fixtures, fixtureMilliseconds,
            device = tuner.DescribeDevice("per-arm:6_6-or-6_7"), deviceRemovalStatus = tuner.DeviceRemovalStatus, runtime,
            compilation = executor.CompilationEvidence, setup, checks, warmup, observations, statuses, errors
        });
    }

    private static void Write(string output, string name, object data)
    {
        string path = Path.Combine(output, name), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonDefaults.Options));
        File.Move(temporary, path, true);
    }

    private sealed record Declaration(string Schema, int Repetitions, int Blocks, int WarmupBatches, int WarmupRepetitions, Cell[] Cells);
    private sealed record Cell(string Id, string Workload, int Count, string Pattern, bool Pairs, int Slots, string[] Arms, Schedule[] Processes);
    private sealed record Schedule(int Index, int[] Seeds, string[][] Orders);
}
