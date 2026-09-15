using HlslPerf.GpuDriven;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class CrowdApplicationTests
{
    private static string Root
    {
        get
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "HlslKernelPipeline.slnx"))) path = path.Parent;
            return path?.FullName ?? throw new InvalidOperationException("Missing repository.");
        }
    }

    [Theory]
    [InlineData("hierarchical")]
    [InlineData("fused")]
    [InlineData("wave-tiled")]
    [InlineData("rts")]
    public void RuntimeAllocationAndSchedulingCannotDependOnExpectedPixels(string arm)
    {
        var scene = new CrowdApplicationScene(4097, 19088743, 15, 64, 32, 3);
        byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
        var a = CrowdApplication.Build(Root, scene, input, arm, new string('a', 64));
        var b = CrowdApplication.Build(Root, scene, input, arm, new string('b', 64));
        Assert.Equal(a.Buffers.Select(x => (x.Name, x.ByteLength)), b.Buffers.Select(x => (x.Name, x.ByteLength)));
        Assert.Equal(a.Passes.Select(x => (x.Name, x.Dispatch)), b.Passes.Select(x => (x.Name, x.Dispatch)));
        Assert.Equal(a.Passes.SelectMany(x => x.Constants), b.Passes.SelectMany(x => x.Constants));
        Assert.Equal(input, a.Buffers.Single(x => x.Name == "agents").InitialData);
        Assert.All(a.Buffers.Where(x => x.Name is not ("agents" or "state")), x => Assert.Null(x.InitialData));
        // Dense and sparse selection have equal worst-case capacities, including guards.
        var dense = CrowdApplication.Build(Root, scene with { VisibilityMask = 0 }, input, arm, new string('a', 64));
        Assert.Equal(a.Buffers.Select(x => x.ByteLength), dense.Buffers.Select(x => x.ByteLength));
        Assert.Equal((scene.AgentCount + 2) * 4, a.Buffers.Single(x => x.Name == "visible-seeds").ByteLength);
    }

    [Fact]
    public void AdvancingSceneTimeKeepsAtlasAddressingLocalAndLeavesOriginalPlanReusable()
    {
        var scene = new CrowdApplicationScene(4097, 99, 7, 64, 32, 3);
        var plan = CrowdApplication.Build(Root, scene, CrowdApplication.GenerateAgents(4097, 99), "rts", new string('a', 64));
        var advance = CrowdApplication.FrameConstants(plan, 24);
        Assert.Equal(26u, advance["f2/raster"][5]);
        Assert.Equal(24u, advance["f2/raster"][2]);
        Assert.Equal(26u, advance["f2/classify"][4]);
        Assert.Equal(26u, advance["f2/tile-scatter"][5]);
        Assert.Equal(2u, plan.Passes.Single(p => p.Name == "f2/raster").Constants[5]);
        Assert.DoesNotContain("f2/scan", advance.Keys);
        Assert.Throws<ArgumentOutOfRangeException>(() => CrowdApplication.FrameConstants(plan, uint.MaxValue));
    }

    [Fact]
    public void InvalidCapacityAndAgentShapeAreRejectedBeforeAnyDeviceIsRequired()
    {
        var s = new CrowdApplicationScene(5, 1, 3);
        Assert.Throws<ArgumentException>(() => CrowdApplication.Build(Root, s, new byte[16], "rts", new string('a', 64)));
        Assert.Throws<ArgumentException>(() => CrowdApplication.Build(Root, s with { VisibilityMask = 5 }, new byte[20], "rts", new string('a', 64)));
        Assert.Throws<ArgumentException>(() => CrowdApplication.Build(Root, s with { Width = 8192 }, new byte[20], "rts", new string('a', 64)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrowdApplication.GenerateAgents(16_777_217, 1));
    }
}
