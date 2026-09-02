using HlslPerf.Core;
using HlslPerf.GpuDriven;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class CrowdVfxWorkloadTests
{
    [Fact]
    public void ProviderExportsStandaloneCrowdWorkload()
    {
        CrowdVfxWorkloadProvider provider = new();

        Assert.Equal([CrowdVfxWorkloadProvider.CrowdVfxWorkloadId], provider.WorkloadIds);
        Assert.Equal(CrowdVfxWorkloadProvider.CrowdVfxWorkloadId, provider.Create(
            CrowdVfxWorkloadProvider.CrowdVfxWorkloadId).Id);
    }

    [Fact]
    public void SmokeBaselineAndFusedPlansShareTheSameApplicationOracle()
    {
        TuningManifest manifest = TuningManifest.Load(ManifestPath("smoke"));
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        CrowdVfxWorkload workload = new();

        KernelExecutionPlan materialized = workload.Build(
            manifest,
            candidates.Single(candidate => candidate.GetRequired("HLSLPERF_CROWD_BACKEND") == 1));
        KernelExecutionPlan fused = workload.Build(
            manifest,
            candidates.Single(candidate => candidate.GetRequired("HLSLPERF_CROWD_BACKEND") == 2));

        Assert.Equal(materialized.ExpectedSha256, fused.ExpectedSha256);
        Assert.Equal(ContentHash.Sha256(workload.ExpectedAtlas.Span), materialized.ExpectedSha256);
        Assert.Equal("frame-atlas", materialized.VerifiedResource);
        Assert.Equal("frame-atlas", fused.VerifiedResource);
        Assert.Contains(materialized.Buffers, buffer => buffer.Name == "visibility-flags");
        Assert.DoesNotContain(fused.Buffers, buffer => buffer.Name == "visibility-flags");
        Assert.Contains(fused.Buffers, buffer => buffer.Name == "crowd-compaction-state");
        Assert.Contains(materialized.Passes, pass => pass.EntryPoint == "BuildCrowdTileHistogram");
        Assert.Contains(fused.Passes, pass => pass.EntryPoint == "FusedCrowdVisibilityCompact");
        Assert.Equal(30, materialized.Passes.Count);
        Assert.Equal(21, fused.Passes.Count);
        Assert.Equal(177, workload.Scene.VisibleCounts.Sum());
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("extreme")]
    public void MeasuredPressureManifestsExposeBoundedConditionalSearchSpace(string level)
    {
        TuningManifest manifest = TuningManifest.Load(ManifestPath(level));
        IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
        KernelCandidate baseline = CandidateGenerator.ResolveBaseline(manifest, candidates);

        Assert.Equal(153, candidates.Count);
        Assert.Equal(1, baseline.GetRequired("HLSLPERF_CROWD_BACKEND"));
        Assert.Equal(256, baseline.GetRequired("HLSLPERF_GROUP_SIZE"));
        Assert.All(
            candidates.Where(candidate => candidate.GetRequired("HLSLPERF_VECTOR_WIDTH") == 4),
            candidate => Assert.Equal(4, candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD")));
        Assert.All(
            candidates.Where(candidate => candidate.GetRequired("HLSLPERF_CROWD_BACKEND") == 1),
            candidate => Assert.False(candidate.Defines.ContainsKey("HLSLPERF_WAVE_SIZE")));
    }

    [Fact]
    public void DeadlineReplayMakesBacklogVisibleWithoutChangingSamples()
    {
        FrameCadenceSnapshot baseline = FrameDeadlineReplay.Replay([12.0], 8.0, 10, 12);
        FrameCadenceSnapshot optimized = FrameDeadlineReplay.Replay([4.0], 8.0, 10, 12);

        Assert.Equal(6, baseline.Completed);
        Assert.Equal(4, baseline.Backlog);
        Assert.Equal(10, baseline.MissedDeadlines);
        Assert.Equal(40, baseline.OutstandingWorkMilliseconds);
        Assert.Equal(10, optimized.Completed);
        Assert.Equal(0, optimized.Backlog);
        Assert.Equal(0, optimized.MissedDeadlines);
        Assert.Equal(0, optimized.OutstandingWorkMilliseconds);
    }

    private static string ManifestPath(string level) => Path.Combine(
        FindRepositoryRoot(),
        "gpu-driven-demo",
        "manifests",
        $"crowd-vfx-{level}.json");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HlslKernelPipeline.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the HlslKernelPipeline repository root.");
    }
}
