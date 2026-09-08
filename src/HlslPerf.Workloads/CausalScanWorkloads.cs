using HlslPerf.Core;

namespace HlslPerf.Workloads;

public static class CausalScanWorkloads
{
    public const string VectorIo = "internal-scan-single-vector-io";
    public static readonly string[] Arms = ["internal-scan-single", VectorIo, "gps-reduce-then-scan"];

    public static UnifiedOperationPlan Build(string repository, UnifiedFixture fixture, string arm)
    {
        if (fixture.Workload != "scan") throw new InvalidDataException("Causal scan accepts scan only.");
        if (!Arms.Contains(arm)) throw new InvalidDataException("Unknown causal scan arm.");
        var original = UnifiedWorkloads.Build(repository, fixture, arm == VectorIo ? "internal-scan-single" : arm);
        if (arm != VectorIo) return original;
        string Rename(string id) => id.Replace("internal-scan-single/", VectorIo + "/", StringComparison.Ordinal);
        var candidate = original with { Implementation = arm,
            Shaders = original.Shaders.Select(shader => shader with { Id = Rename(shader.Id),
                Defines = new Dictionary<string, string>(shader.Defines) { ["HLSLPERF_VECTOR_WIDTH"] = "4" } }).ToArray(),
            Passes = original.Passes.Select(pass => pass with { ShaderId = pass.ShaderId is null ? null : Rename(pass.ShaderId) }).ToArray() };
        candidate.Validate(); return candidate;
    }
}
