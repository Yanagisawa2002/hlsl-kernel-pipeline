using HlslPerf.Core;
using HlslPerf.GpuDriven;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class CrowdCpuRendererTests
{
    [Theory]
    [InlineData(1, 0u)]
    [InlineData(31, 3u)]
    [InlineData(4097, 15u)]
    [InlineData(8193, 0x7fffffffu)]
    public void ParallelRendererMatchesUnchangedOracleThroughPoisonedReuse(int n, uint mask)
    {
        var scene = new CrowdApplicationScene(n, 19088743, mask, 64, 32, 3);
        byte[] input = CrowdApplication.GenerateAgents(n, scene.Seed), before = input.ToArray();
        byte[] expected = Oracle(scene, 6);
        foreach (int workers in new[] { 1, CrowdCpuRenderer.MaximumWorkers(scene) }.Distinct())
        {
            using var renderer = new CrowdCpuRenderer(scene, input, workers);
            foreach (byte poison in new byte[] { 0xa5, 0x5a })
            for (int window = 0; window < 2; window++)
            {
                renderer.PoisonOutput(poison);
                var timing = renderer.Render((uint)(window * scene.Frames));
                Assert.Equal(expected.AsSpan(window * renderer.Data.Length, renderer.Data.Length).ToArray(), renderer.Data.ToArray());
                Assert.InRange(timing.PeakWorkers, 1, workers);
                Assert.Equal(before, input);
            }
        }
    }

    [Fact]
    public void HigherWorkerBudgetSharesStorageAndCannotSilentlyReduceWorkers()
    {
        var scene = new CrowdApplicationScene(4097, 69501203, 0);
        byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
        using var serial = new CrowdCpuRenderer(scene, input, 1);
        using var parallel = new CrowdCpuRenderer(scene, input, CrowdCpuRenderer.MaximumWorkers(scene));
        Assert.Equal(serial.LogicalBytes, parallel.LogicalBytes);
        Assert.Equal(CrowdCpuRenderer.MaximumWorkers(scene), parallel.Workers);
        Assert.Throws<InvalidOperationException>(() => new CrowdCpuRenderer(scene, input, parallel.Workers, parallel.LogicalBytes - 1));
        Assert.Throws<ArgumentException>(() => new CrowdCpuRenderer(scene, input, 0));
        Assert.Throws<ArgumentException>(() => new CrowdCpuRenderer(scene, input, CrowdCpuRenderer.MaximumWorkers(scene) + 1));
    }

    [Fact]
    public void AdvancingFramesChangesTheImageAndReleasedRendererCannotRun()
    {
        var scene = new CrowdApplicationScene(4097, 19088743, 0, 64, 32, 3);
        byte[] input = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
        var renderer = new CrowdCpuRenderer(scene, input, 1);
        renderer.Render(0); byte[] first = renderer.Data.ToArray();
        renderer.Render(3); Assert.NotEqual(first, renderer.Data.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(100001));
        renderer.Dispose(); Assert.Throws<ObjectDisposedException>(() => renderer.Render(0));
    }

    private static byte[] Oracle(CrowdApplicationScene scene, int frames)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HlslKernelPipeline.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Repository root not found.");
        TuningManifest manifest = new()
        {
            Name = "independent-cpu-oracle", KernelPath = Path.Combine(root.FullName, "gpu-driven-demo/crowd-vfx.hlsl"),
            ShaderModel = "6_6", Axes = [], Workload = new() { Id = "crowd-vfx-gpu-driven-v1", Parameters = new Dictionary<string, long>
            { ["agentCount"] = scene.AgentCount, ["seed"] = scene.Seed, ["visibilityMask"] = scene.VisibilityMask,
              ["width"] = scene.Width, ["height"] = scene.Height, ["frameCount"] = frames, ["tileSize"] = 16 } }
        };
        var candidate = new KernelCandidate(new Dictionary<string, int>
        { ["HLSLPERF_CROWD_BACKEND"] = 1, ["HLSLPERF_GROUP_SIZE"] = 256, ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
          ["HLSLPERF_VECTOR_WIDTH"] = 1, ["HLSLPERF_CROWD_TILE_SIZE"] = 16, ["HLSLPERF_CROWD_TILE_BINS"] = 8 });
        var oracle = new CrowdVfxWorkload(); oracle.Build(manifest, candidate); return oracle.ExpectedAtlas.ToArray();
    }
}
