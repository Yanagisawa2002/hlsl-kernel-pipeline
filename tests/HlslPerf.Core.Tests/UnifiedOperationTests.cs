using System.Runtime.InteropServices;
using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class UnifiedOperationTests
{
    private static uint[] Values(byte[] bytes) => MemoryMarshal.Cast<byte, uint>(bytes).ToArray();

    [Fact]
    public void ScanOracleRetainsFullUint32WrappingSemantics()
    {
        var fixture = UnifiedWorkloads.Fixture("scan", 4, 1, explicitInput: [uint.MaxValue, 1, uint.MaxValue, 2]);
        Assert.Equal(new uint[] { 0, uint.MaxValue, 0, uint.MaxValue }, Values(fixture.ExpectedKeys));
        Assert.Equal(new uint[] { uint.MaxValue, 1, uint.MaxValue, 2 }, Values(fixture.Input));
    }

    [Fact]
    public void DuplicateKeysKeepOriginalPayloadOrder()
    {
        var fixture = UnifiedWorkloads.Fixture("radix", 5, 1, pairs: true, explicitInput: [2, 1, 2, 1, 0]);
        Assert.Equal(new uint[] { 0, 1, 1, 2, 2 }, Values(fixture.ExpectedKeys));
        Assert.Equal(new uint[] { 4, 1, 3, 0, 2 }, Values(fixture.ExpectedPayloads!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmdRestoresOriginalInputBeforeEveryFullKeyOperation(bool pairs)
    {
        var fixture = UnifiedWorkloads.Fixture("radix", 1025, 3, "duplicate", pairs);
        var plan = UnifiedWorkloads.Build(".", fixture, "amd-parallel-sort");
        Assert.Equal(UnifiedStage.InputRestore, plan.Passes[0].Stage);
        Assert.Equal(40, plan.Passes.Count(pass => pass.Stage == UnifiedStage.Algorithm));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (uint)(i * 4)),
            plan.Passes.Where(pass => pass.Name.EndsWith("scatter")).Select(pass => pass.Constants[6]));
        Assert.Equal(fixture.InputSha256, ContentHash.Sha256(plan.Buffers.Single(buffer => buffer.Name == "input").InitialData!));
        Assert.Equal(pairs ? 2 : 1, plan.Outputs.Count);
        Assert.All(plan.Outputs, output => Assert.DoesNotContain(output.Resource, plan.ImmutableInputs));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3071)]
    [InlineData(3072)]
    [InlineData(3073)]
    public void UpstreamScanHandlesVectorTailsThroughExplicitConsumerConversion(int count)
    {
        var fixture = UnifiedWorkloads.Fixture("scan", count, 3);
        foreach (string implementation in new[] { "gps-reduce-then-scan", "gps-decoupled-fallback" })
        {
            var plan = UnifiedWorkloads.Build(".", fixture, implementation);
            Assert.Equal(0, plan.Buffers.Single(buffer => buffer.Name == "input").ByteLength % 16);
            Assert.Equal(count * 4, plan.Buffers.Single(buffer => buffer.Name == plan.Outputs[0].Resource).ByteLength);
            Assert.Equal(count % 4 == 0 ? 0 : 1, plan.Passes.Count(pass => pass.Stage == UnifiedStage.OutputConversion));
            if (implementation.EndsWith("fallback"))
            {
                Assert.Equal("InitCSDLDF", plan.Passes[0].Name);
                Assert.Equal(UnifiedStage.ScratchInitialization, plan.Passes[0].Stage);
                Assert.Equal(256u, plan.Passes[0].Dispatch!.X);
            }
        }
    }

    [Fact]
    public void EmptyOperationsHaveAnExplicitGuardForEveryImplementation()
    {
        foreach (string workload in new[] { "scan", "radix" })
            foreach (string implementation in workload == "scan" ? UnifiedWorkloads.ScanImplementations : UnifiedWorkloads.RadixImplementations)
            {
                var plan = UnifiedWorkloads.Build(".", UnifiedWorkloads.Fixture(workload, 0, 3), implementation);
                Assert.Empty(plan.Shaders);
                Assert.All(plan.Passes, pass => Assert.NotNull(pass.CopySource));
            }
    }

    [Fact]
    public void SrvUavAliasingCannotAccidentallyEnterTheUnifiedExecutor()
    {
        var plan = UnifiedWorkloads.Build(".", UnifiedWorkloads.Fixture("scan", 4, 3), "gps-reduce-then-scan");
        var pass = plan.Passes[0] with { Srvs = ["input"] };
        Assert.Throws<InvalidDataException>(() => (plan with { Passes = [pass] }).Validate());
    }
}
