using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

[assembly: SupportedOSPlatform("windows10.0")]

if (args.Length != 2) throw new ArgumentException("Usage: ScanCorrectness <declaration.json> <new-output-directory>");
string declarationText = File.ReadAllText(args[0]);
using JsonDocument declaration = JsonDocument.Parse(declarationText);
string output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Correctness output already exists; preserve it and use a new declared attempt.");
Directory.CreateDirectory(output);
using D3D12Tuner tuner = new("R9700");
List<object> results = [];
bool passed = true;
DateTimeOffset started = DateTimeOffset.UtcNow;
foreach (JsonElement cell in declaration.RootElement.GetProperty("correctnessCells").EnumerateArray())
{
    string manifestPath = cell.GetProperty("manifestPath").GetString()!;
    TuningManifest manifest = TuningManifest.Load(manifestPath);
    foreach (KernelCandidate candidate in CandidateGenerator.Expand(manifest))
    {
        string name = cell.GetProperty("name").GetString()!;
        try
        {
            var scenario = new WorkloadScenario(name,
                cell.GetProperty("seeds").EnumerateArray().Select(s => s.GetInt32()).ToArray(),
                cell.GetProperty("elementCount").GetInt32(), 536870912);
            using var session = tuner.PrepareScenario(manifest, manifestPath,
                BuiltinWorkloads.Resolve(manifest), candidate, scenario);
            var verification = session.VerifyAll();
            bool ok = verification.All(v => v.Correctness.Passed);
            passed &= ok;
            results.Add(new { name, candidateId = candidate.Id, candidate.Defines, passed = ok,
                session.Evidence, verification });
            Console.WriteLine($"{name}, backend {candidate.Defines["HLSLPERF_SCAN_BACKEND"]}: {ok}");
        }
        catch (Exception error)
        {
            passed = false;
            results.Add(new { name, candidateId = candidate.Id, passed = false, error = error.ToString() });
            Console.WriteLine(error);
        }
        Save();
    }
}
Save();
return passed ? 0 : 2;

void Save()
{
    var report = new { schema = "hlslperf.scan-correctness.v1", purpose = "Correctness only; no performance claim",
        startedUtc = started, updatedUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
        processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(), passed,
        declarationSha256 = ContentHash.Sha256(declarationText), device = tuner.DescribeDevice("6_6"), results };
    File.WriteAllText(Path.Combine(output, "correctness.json"), JsonSerializer.Serialize(report, JsonDefaults.Options));
}
