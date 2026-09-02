using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class WorkloadPackTests
{
    [Fact]
    public void ReductionBuildsRecursiveEndToEndPlan()
    {
        TuningManifest manifest = Manifest(
            "reduction-u32-v1",
            new Dictionary<string, long> { ["elementCount"] = 1000, ["seed"] = 7 },
            [
                new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] },
                new CandidateAxis { Name = "HLSLPERF_ELEMENTS_PER_THREAD", Values = [2] }
            ]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2
            }));

        Assert.Equal("reduction-u32-v1", plan.WorkloadId);
        Assert.True(plan.Passes.Count >= 2);
        Assert.Equal(4, plan.Buffers.Single(buffer => buffer.Name == plan.VerifiedResource).ByteLength);
    }

    [Fact]
    public void ScanBuildsHierarchyAndOffsetPropagation()
    {
        TuningManifest manifest = Manifest(
            "exclusive-scan-u32-v1",
            new Dictionary<string, long> { ["elementCount"] = 1000, ["seed"] = 7 },
            [
                new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] },
                new CandidateAxis { Name = "HLSLPERF_ELEMENTS_PER_THREAD", Values = [2] }
            ]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2
            }));

        Assert.Contains(plan.Passes, pass => pass.EntryPoint == "BlockScanPass");
        Assert.Contains(plan.Passes, pass => pass.EntryPoint == "AddScanOffsets");
        Assert.Equal(4000, plan.Buffers.Single(buffer => buffer.Name == plan.VerifiedResource).ByteLength);
    }

    [Fact]
    public void ScanSinglePassBuildsEpochResetAndOneDataDispatch()
    {
        TuningManifest manifest = Manifest(
            "exclusive-scan-u32-v1",
            new Dictionary<string, long> { ["elementCount"] = 1000, ["seed"] = 7 },
            [
                new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] },
                new CandidateAxis { Name = "HLSLPERF_ELEMENTS_PER_THREAD", Values = [2] },
                new CandidateAxis { Name = "HLSLPERF_SCAN_BACKEND", Values = [3] }
            ]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
                ["HLSLPERF_SCAN_BACKEND"] = 3
            }));

        Assert.Collection(
            plan.Passes,
            pass => Assert.Equal("ResetSinglePassState", pass.EntryPoint),
            pass => Assert.Equal("SinglePassScan", pass.EntryPoint));
        Assert.Equal(8u, plan.Passes[1].Dispatch.X);
        Assert.Equal(8 + 8 * 12, plan.Buffers.Single(buffer => buffer.Name == "single-pass-state").ByteLength);
        Assert.Equal(4000, plan.Buffers.Single(buffer => buffer.Name == plan.VerifiedResource).ByteLength);
    }

    [Fact]
    public void TransposeUsesTwoDimensionalDispatchAndCandidateSpecificTile()
    {
        TuningManifest manifest = Manifest(
            "transpose-u32-v1",
            new Dictionary<string, long> { ["width"] = 17, ["height"] = 13, ["seed"] = 7 },
            [
                new CandidateAxis { Name = "HLSLPERF_TILE_DIM", Values = [8] },
                new CandidateAxis { Name = "HLSLPERF_BLOCK_ROWS", Values = [16] }
            ]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_TILE_DIM"] = 8,
                ["HLSLPERF_BLOCK_ROWS"] = 16
            }));

        Assert.Equal(3u, plan.Passes.Single().Dispatch.X);
        Assert.Equal(2u, plan.Passes.Single().Dispatch.Y);
        Assert.Equal(17 * 13 * 4, plan.Buffers.Single(buffer => buffer.Name == "output").ByteLength);
    }

    private static TuningManifest Manifest(
        string workloadId,
        IReadOnlyDictionary<string, long> parameters,
        IReadOnlyList<CandidateAxis> axes) =>
        new()
        {
            SchemaVersion = "2.0",
            Name = "test",
            KernelPath = "test.hlsl",
            WorkItemCount = 1000,
            MeasurementBatches = 3,
            Workload = new WorkloadSpec { Id = workloadId, Parameters = parameters },
            Axes = axes
        };
}
