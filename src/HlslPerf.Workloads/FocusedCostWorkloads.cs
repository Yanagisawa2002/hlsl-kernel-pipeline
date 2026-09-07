using HlslPerf.Core;

namespace HlslPerf.Workloads;

public static class FocusedCostWorkloads
{
    public const string RadixBallot = "internal-radix-8-ballot";
    public const string ScanCounters = "internal-scan-single-counters";

    public static UnifiedOperationPlan Build(string repository, UnifiedFixture fixture, string arm)
    {
        if (arm == RadixBallot)
        {
            var original = UnifiedWorkloads.Build(repository, fixture, "internal-radix-8");
            string RenameRadix(string id) => id.Replace("internal-radix-8/", RadixBallot + "/", StringComparison.Ordinal);
            var candidate = original with { Implementation = arm,
                Shaders = original.Shaders.Select(shader => shader with { Id = RenameRadix(shader.Id),
                    Defines = new Dictionary<string, string>(shader.Defines) { ["HLSLPERF_RADIX_RANK_BALLOT"] = "1" } }).ToArray(),
                Passes = original.Passes.Select(pass => pass with { ShaderId = pass.ShaderId is null ? null : RenameRadix(pass.ShaderId) }).ToArray() };
            candidate.Validate(); return candidate;
        }
        bool counters = arm == ScanCounters;
        var plan = UnifiedWorkloads.Build(repository, fixture, counters ? "internal-scan-single" : arm);
        if (!counters) return plan;
        if (fixture.Workload != "scan" || fixture.Count == 0) throw new InvalidDataException("Counters require nonempty scan.");
        int blocks = (fixture.Count + 4095) / 4096;
        string Rename(string id) => id.Replace("internal-scan-single/", ScanCounters + "/", StringComparison.Ordinal);
        var shaders = plan.Shaders.Select(shader => shader with { Id = Rename(shader.Id), Defines = new Dictionary<string, string>(shader.Defines) { ["HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS"] = "1" } }).ToArray();
        var passes = plan.Passes.Select(pass => pass with { ShaderId = Rename(pass.ShaderId!),
            Uavs = pass.Name == "single-pass-scan" ? [pass.Uavs[0], pass.Uavs[1], "diagnostic-counters"] : pass.Uavs }).ToArray();
        plan = plan with { Implementation = arm, Shaders = shaders, Passes = passes,
            Buffers = plan.Buffers.Append(new KernelBufferSpec("diagnostic-counters", blocks * 16)).ToArray() };
        plan.Validate(); return plan;
    }
}
