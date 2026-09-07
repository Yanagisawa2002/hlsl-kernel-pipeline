using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class WorkloadScenarioTests
{
    private static TuningManifest Manifest() => new()
    {
        SchemaVersion = "2.0", Name = "scenario-test", KernelPath = "scan.hlsl",
        Workload = new WorkloadSpec { Id = "exclusive-scan-u32-v1", Parameters = new Dictionary<string,long> { ["elementCount"] = 1025, ["seed"] = 17 } },
        Axes = [new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] }]
    };
    private static KernelCandidate Candidate() => new(new Dictionary<string,int>
    {
        ["HLSLPERF_GROUP_SIZE"] = 64, ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4, ["HLSLPERF_SCAN_BACKEND"] = 3
    });

    [Fact]
    public void RotatingSeedsChangeRealInputAndCpuOracleAndReplayExactly()
    {
        TuningManifest manifest = Manifest();
        IKernelWorkload workload = BuiltinWorkloads.Resolve(manifest);
        WorkloadScenario scenario = new("rotating", [101, 202, 303], 4097);
        var first = scenario.Build(manifest, workload, Candidate());
        var replay = scenario.Build(manifest, workload, Candidate());
        Assert.Equal(3, first.Select(slot => slot.InputSha256).Distinct().Count());
        Assert.Equal(3, first.Select(slot => slot.Plan.ExpectedSha256).Distinct().Count());
        Assert.Equal(first.Select(slot => slot.InputSha256), replay.Select(slot => slot.InputSha256));
        Assert.All(first, slot => Assert.Equal(4097, slot.Plan.LogicalItemCount));
        Assert.Equal(17, manifest.Workload!.Parameters["seed"]);
        Assert.Contains("uncontrolled", scenario.CachePolicy);
    }

    [Fact]
    public void OversizedAndDuplicateSeedScenariosFailBeforeGpuAllocation()
    {
        TuningManifest manifest = Manifest();
        Assert.Throws<InvalidDataException>(() => new WorkloadScenario("bad", [1,1]).Validate());
        Assert.Throws<InvalidDataException>(() => new WorkloadScenario("too-large", [1,2], null, 1024)
            .Build(manifest, BuiltinWorkloads.Resolve(manifest), Candidate()));
    }

    [Fact]
    public void WorkloadThatIgnoresSeedCannotClaimChangingInputs()
    {
        Assert.Throws<InvalidDataException>(() => new WorkloadScenario("unsupported", [1,2])
            .Build(Manifest(), new ConstantWorkload(), Candidate()));
    }

    private sealed class ConstantWorkload : IKernelWorkload
    {
        public string Id => "constant";
        public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate) => new(Id,
            KernelAbiV1.Id, 1, [new("input", 4, new byte[4]), new("output", 4)],
            [new("test", "Main", new(1), "input", null, "output", null, [])], "output", new string('a',64));
    }
}

