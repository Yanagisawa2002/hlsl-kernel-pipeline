using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class DynamicPlanTests
{
    private static KernelExecutionPlan Plan(int count = 65, int limit = 65) => new DynamicCompactionWorkload().Build(
        new TuningManifest
        {
            Name = "test", KernelPath = "unused", KernelAbiVersion = KernelAbiV2.Id, Axes = [],
            Workload = new() { Id = "dynamic-compaction-consume-u32-v2", Parameters = new Dictionary<string, long>
                { ["elementCount"] = count, ["maximumItems"] = limit, ["activeMode"] = 2, ["argumentByteOffset"] = 16, ["countByteOffset"] = 16 } }
        }, new KernelCandidate(new Dictionary<string, int> { ["HLSLPERF_GROUP_SIZE"] = 64 }));

    [Fact]
    public void ZeroCountRequiresV2AndRetainsAllOutputOracles()
    {
        KernelExecutionPlan plan = Plan(0, 0);
        plan.Validate();
        Assert.Equal(5, plan.GetVerifiedOutputs().Count);
        Assert.Throws<InvalidDataException>(() => (plan with { AbiVersion = KernelAbiV1.Id }).Validate());
    }

    [Theory]
    [InlineData(-4)] [InlineData(2)] [InlineData(24)] [InlineData(int.MaxValue - 3)]
    public void RejectsInvalidOrOverflowingArgumentRange(int offset)
    {
        KernelExecutionPlan plan = Plan();
        KernelPassSpec consumer = plan.Passes[^1];
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [.. plan.Passes.Take(2),
            consumer with { Indirect = consumer.Indirect! with { ArgumentByteOffset = offset } }] }).Validate());
    }

    [Theory]
    [InlineData(-4)] [InlineData(3)] [InlineData(24)] [InlineData(int.MaxValue - 3)]
    public void RejectsInvalidOrOverflowingCountRange(int offset)
    {
        KernelExecutionPlan plan = Plan();
        KernelPassSpec consumer = plan.Passes[^1];
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [.. plan.Passes.Take(2),
            consumer with { Indirect = consumer.Indirect! with { CountByteOffset = offset } }] }).Validate());
    }

    [Fact]
    public void MaximumDispatchBoundUsesWideArithmetic()
    {
        KernelExecutionPlan plan = Plan();
        var resources = plan.Buffers.ToDictionary(buffer => buffer.Name);
        KernelIndirectDispatch indirect = plan.Passes[^1].Indirect!;
        (indirect with { MaximumItemCount = 65_535 * 1024u, ThreadsPerGroup = 1024 }).Validate(resources);
        Assert.Throws<InvalidDataException>(() => (indirect with { MaximumItemCount = 65_535 * 1024u + 1, ThreadsPerGroup = 1024 }).Validate(resources));
        Assert.Throws<InvalidDataException>(() => (indirect with { MaximumItemCount = uint.MaxValue }).Validate(resources));
        Assert.Throws<InvalidDataException>(() => (indirect with { ThreadsPerGroup = 0 }).Validate(resources));
    }

    [Fact]
    public void RequiresExplicitProducerAndRejectsCycles()
    {
        KernelExecutionPlan plan = Plan();
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [.. plan.Passes.Take(2), plan.Passes[^1] with { DependsOn = [] }] }).Validate());
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [plan.Passes[0] with { DependsOn = ["consume-and-bin"] }, .. plan.Passes.Skip(1)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [plan.Passes[^1] with { DependsOn = [] }] }).Validate());
    }

    [Fact]
    public void PreventsShaderWritesToExecutorOwnedArgumentsAndDuplicateOracles()
    {
        KernelExecutionPlan plan = Plan();
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [plan.Passes[0] with { Output1 = "arguments" }, .. plan.Passes.Skip(1)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (plan with { AdditionalVerifiedOutputs = [.. plan.AdditionalVerifiedOutputs, new(plan.VerifiedResource, plan.ExpectedSha256)] }).Validate());
    }

    [Fact]
    public void V2AllowsZeroManifestParametersWithoutWeakeningLegacyValidation()
    {
        TuningManifest Make(string abi) => new()
        {
            SchemaVersion = "3.0", Name = "zero", KernelPath = "unused", KernelAbiVersion = abi, WorkItemCount = 0,
            Workload = new() { Id = "test", Parameters = new Dictionary<string, long> { ["elementCount"] = 0 } },
            Axes = [new() { Name = "GROUP", Values = [64] }]
        };
        Make(KernelAbiV2.Id).Validate();
        Assert.Throws<InvalidDataException>(() => Make(KernelAbiV1.Id).Validate());
    }

    [Fact]
    public void RejectsUnknownInputAndReadBeforeInitialization()
    {
        KernelExecutionPlan plan = Plan();
        Assert.Throws<InvalidDataException>(() => (plan with { Buffers = plan.Buffers.Select(buffer => buffer.Name == "input" ? buffer with { InitialData = null } : buffer).ToArray() }).Validate());
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [plan.Passes[0] with { Input0 = "missing" }, .. plan.Passes.Skip(1)] }).Validate());
    }
}
