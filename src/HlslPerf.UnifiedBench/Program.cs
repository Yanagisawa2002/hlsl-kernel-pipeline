using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

[assembly: SupportedOSPlatform("windows10.0")]

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("D3D12 requires Windows.");
if (args.Length >= 3 && args[0] is "correctness" or "formal" or "pilot") return UnifiedBenchRunner.Run(args);
if (args.Length < 3 || args[0] != "diagnose")
{
    Console.Error.WriteLine("Usage: hlslperf-unified diagnose <repository> <new-output-directory> [workload] [count] [pattern] [pairs]");
    return 1;
}
string repository = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new InvalidDataException("Refusing to overwrite a diagnostic output directory.");
Directory.CreateDirectory(output);
string workload = args.Length > 3 ? args[3] : "scan";
int count = args.Length > 4 ? int.Parse(args[4]) : 6145;
string pattern = args.Length > 5 ? args[5] : "uniform";
bool pairs = args.Length > 6 && bool.Parse(args[6]);
string? selected = args.Length > 7 && args[7] != "all" ? args[7] : null;
bool debug = args.Length > 8 && bool.Parse(args[8]);
bool tracePasses = debug;
string? debugUnavailable = null;
DateTimeOffset started = DateTimeOffset.UtcNow;
List<object> results = [];
bool allPassed = true;
long fixtureStart = Stopwatch.GetTimestamp();
UnifiedFixture fixture = UnifiedWorkloads.Fixture(workload, count, 771029, pattern, pairs);
double fixtureMilliseconds = Stopwatch.GetElapsedTime(fixtureStart).TotalMilliseconds;
if (debug)
{
    try { D3D12Tuner.EnableUnifiedDebugLayer(); }
    catch (Exception error) { debugUnavailable = error.ToString(); debug = false; Console.WriteLine("Debug layer unavailable; retaining dispatch isolation and device-reason checks."); }
}
using D3D12Tuner tuner = new("R9700");
using D3D12Tuner.UnifiedExecutor executor = tuner.CreateUnifiedExecutor();
foreach (string implementation in workload == "scan" ? UnifiedWorkloads.ScanImplementations : UnifiedWorkloads.RadixImplementations)
{
    if (selected is not null && implementation != selected) continue;
    try
    {
        long planStart = Stopwatch.GetTimestamp();
        UnifiedOperationPlan plan = UnifiedWorkloads.Build(repository, fixture, implementation);
        double planMilliseconds = Stopwatch.GetElapsedTime(planStart).TotalMilliseconds;
        using D3D12Tuner.UnifiedSession session = executor.Prepare(plan);
        if (tracePasses) session.DiagnosePasses((pass, milliseconds) =>
        {
            results.Add(new { implementation, diagnosticPass = pass, milliseconds });
            Console.WriteLine($"{implementation}/{pass}: {milliseconds} ms");
            Save();
        });
        var verification = session.Verify((resource, data) =>
        {
            if (count <= 65536) File.WriteAllBytes(Path.Combine(output, implementation + "-" + resource + ".bin"), data.ToArray());
        });
        bool passed = verification.All(result => result.Passed);
        allPassed &= passed;
        // Development-only timing is retained explicitly and is not formal confirmation.
        UnifiedBatchTiming? timing = passed ? session.MeasureBatch(6) : null;
        results.Add(new { implementation, passed, planMilliseconds, session.CpuPreparationMilliseconds,
            session.GpuUploadMilliseconds, session.UploadBytes, session.CommittedBytes, session.LogicalBytes,
            verification, timing, passes = plan.Passes, buffers = plan.Buffers.Select(buffer => new { buffer.Name, buffer.ByteLength, initialized = buffer.InitialData is not null }) });
        Console.WriteLine($"{implementation}: correctness {passed}");
    }
    catch (Exception error)
    {
        allPassed = false;
        results.Add(new { implementation, passed = false, error = error.ToString() });
        Console.WriteLine($"{implementation}: {error.Message}");
        if (tuner.IsDeviceRemoved) { Save(); break; }
    }
    Save();
}
Save();
return allPassed ? 0 : 2;

void Save() => File.WriteAllText(Path.Combine(output, "diagnostic.json"), JsonSerializer.Serialize(new
{
    schema = "hlslperf.unified-diagnostic.v1", developmentOnly = true, testsPassed = allPassed, pid = Environment.ProcessId,
    startedUtc = started, recordedUtc = DateTimeOffset.UtcNow, fixture, fixtureMilliseconds,
    device = tuner.DescribeDevice("6_6"), debug, tracePasses, debugUnavailable, deviceRemovalStatus = tuner.DeviceRemovalStatus,
    debugMessages = debug ? tuner.ReadUnifiedDebugMessages() : [], compilation = executor.CompilationEvidence, results
}, JsonDefaults.Options));
