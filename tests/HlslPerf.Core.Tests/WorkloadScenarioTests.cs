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

    [Fact]
    public void RootConstantSeedIsActualInputEvenWithoutUploadedBuffers()
    {
        var manifest = new TuningManifest {
            Name = "constant-input", KernelPath = "uint-mix.hlsl", WorkItemCount = 17,
            Axes = [new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] }],
            FixedDefines = new Dictionary<string, int> { ["HLSLPERF_ELEMENTS_PER_THREAD"] = 1, ["HLSLPERF_ALU_ROUNDS"] = 1 }
        };
        var candidate = CandidateGenerator.Expand(manifest).Single();
        var slots = new WorkloadScenario("root-input", [101, 202, 303]).Build(manifest, BuiltinWorkloads.Resolve(manifest), candidate);
        Assert.All(slots, s => Assert.All(s.Plan.Buffers, b => Assert.Null(b.InitialData)));
        Assert.Equal(3, slots.Select(s => s.InputSha256).Distinct().Count());
        Assert.Equal(3, slots.Select(s => s.Plan.ExpectedSha256).Distinct().Count());
        Assert.Equal(new uint[] { 101, 202, 303 }, slots.Select(s => s.Plan.Passes[0].Constants[1]));
    }

    private sealed class ConstantWorkload : IKernelWorkload
    {
        public string Id => "constant";
        public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate) => new(Id,
            KernelAbiV1.Id, 1, [new("input", 4, new byte[4]), new("output", 4)],
            [new("test", "Main", new(1), "input", null, "output", null, [])], "output", new string('a',64));
    }

    [Fact]
    public void ProviderStagingArrayReuseCannotRewriteAnEarlierResidentSlot()
    {
        var slots = new WorkloadScenario("mutable-provider", [1,2]).Build(Manifest(), new ReusingWorkload(), Candidate());
        Assert.Equal(1, slots[0].Plan.Buffers[0].InitialData![0]);
        Assert.Equal(2, slots[1].Plan.Buffers[0].InitialData![0]);
    }

    private sealed class ReusingWorkload : IKernelWorkload
    {
        private readonly byte[] input = new byte[4];
        public string Id => "reuse";
        public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
        {
            input[0] = (byte)manifest.Workload!.GetRequiredInt32("seed");
            return new(Id, KernelAbiV1.Id, 1, [new("input", 4, input), new("output",4)],
                [new("test", "Main", new(1), "input", null, "output", null, [])], "output", ContentHash.Sha256(input));
        }
    }
}

