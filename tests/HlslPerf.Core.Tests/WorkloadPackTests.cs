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
    public void SegmentedScanBuildsPairLookbackStateAndTwoInputPlan()
    {
        TuningManifest manifest = Manifest(
            "segmented-exclusive-scan-u32-v1",
            new Dictionary<string, long>
            {
                ["elementCount"] = 1000,
                ["seed"] = 7,
                ["averageSegmentLength"] = 16
            },
            [
                new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] },
                new CandidateAxis { Name = "HLSLPERF_ELEMENTS_PER_THREAD", Values = [2] }
            ]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
                ["HLSLPERF_ITEMS_SCALE"] = 2,
                ["HLSLPERF_VECTOR_WIDTH"] = 1,
                ["HLSLPERF_PERSISTENT_GROUPS"] = 8
            }));

        Assert.Collection(
            plan.Passes,
            pass =>
            {
                Assert.Equal("ResetSegmentedScanState", pass.EntryPoint);
                Assert.Equal("segmented-state", pass.Output1);
            },
            pass =>
            {
                Assert.Equal("SegmentedScanSinglePass", pass.EntryPoint);
                Assert.Equal("values", pass.Input0);
                Assert.Equal("heads", pass.Input1);
            });
        Assert.Equal(8 + 4 * 20, plan.Buffers.Single(buffer => buffer.Name == "segmented-state").ByteLength);
    }

    [Fact]
    public void CompactionExposesUnfusedAndFusedEndToEndPlans()
    {
        TuningManifest manifest = Manifest(
            "stream-compaction-u32-v1",
            new Dictionary<string, long>
            {
                ["elementCount"] = 1000,
                ["seed"] = 7,
                ["predicateMask"] = 7
            },
            [
                new CandidateAxis { Name = "HLSLPERF_SCAN_BACKEND", Values = [1, 3] },
                new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] },
                new CandidateAxis { Name = "HLSLPERF_ELEMENTS_PER_THREAD", Values = [2] }
            ]);
        IKernelWorkload workload = BuiltinWorkloads.Resolve(manifest);
        KernelExecutionPlan unfused = workload.Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_SCAN_BACKEND"] = 1,
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
                ["HLSLPERF_VECTOR_WIDTH"] = 1
            }));
        KernelExecutionPlan fused = workload.Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_SCAN_BACKEND"] = 3,
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
                ["HLSLPERF_VECTOR_WIDTH"] = 1,
                ["HLSLPERF_SINGLE_PASS_ITEMS_SCALE"] = 2,
                ["HLSLPERF_SINGLE_PASS_GROUPS"] = 8
            }));

        Assert.Contains(unfused.Passes, pass => pass.EntryPoint == "ProduceCompactionFlags");
        Assert.Contains(unfused.Passes, pass => pass.EntryPoint == "BlockScanPass");
        Assert.Contains(unfused.Passes, pass => pass.EntryPoint == "ScatterCompactedValues");
        Assert.Collection(
            fused.Passes,
            pass =>
            {
                Assert.Equal("ResetSinglePassState", pass.EntryPoint);
                Assert.Equal("compaction-state", pass.Output0);
                Assert.Null(pass.Output1);
            },
            pass => Assert.Equal("FusedCompactSinglePass", pass.EntryPoint));
    }

    [Fact]
    public void HistogramProducesOffsetsIncludingTerminalCount()
    {
        TuningManifest manifest = Manifest(
            "histogram-prefix-u32-v1",
            new Dictionary<string, long>
            {
                ["elementCount"] = 1000,
                ["seed"] = 7,
                ["binCount"] = 256
            },
            [new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [128] }]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_HISTOGRAM_BINS"] = 256,
                ["HLSLPERF_HISTOGRAM_BACKEND"] = 2,
                ["HLSLPERF_HISTOGRAM_REPLICAS"] = 2,
                ["HLSLPERF_GROUP_SIZE"] = 128,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
                ["HLSLPERF_VECTOR_WIDTH"] = 4
            }));

        Assert.Equal(
            ["ResetHistogram", "BuildHistogram", "PrefixHistogram"],
            plan.Passes.Select(pass => pass.EntryPoint));
        Assert.Equal(257 * sizeof(uint), plan.Buffers.Single(buffer => buffer.Name == "offsets").ByteLength);
    }

    [Fact]
    public void HistogramCountsAllGroupsharedArraysAgainstLdsBudget()
    {
        TuningManifest manifest = Manifest(
            "histogram-prefix-u32-v1",
            new Dictionary<string, long>
            {
                ["elementCount"] = 1000,
                ["seed"] = 7,
                ["binCount"] = 1024
            },
            [new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [128] }]);
        IKernelWorkload workload = BuiltinWorkloads.Resolve(manifest);
        KernelCandidate candidate = new(new Dictionary<string, int>
        {
            ["HLSLPERF_HISTOGRAM_BINS"] = 1024,
            ["HLSLPERF_HISTOGRAM_BACKEND"] = 2,
            ["HLSLPERF_HISTOGRAM_REPLICAS"] = 8,
            ["HLSLPERF_GROUP_SIZE"] = 128,
            ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
            ["HLSLPERF_VECTOR_WIDTH"] = 4
        });

        Assert.Throws<InvalidDataException>(() => workload.Build(manifest, candidate));
    }

    [Fact]
    public void RadixSortReusesOneScratchHierarchyAcrossAllBits()
    {
        TuningManifest manifest = Manifest(
            "radix-sort-u32-v1",
            new Dictionary<string, long>
            {
                ["elementCount"] = 1000,
                ["seed"] = 7,
                ["bitCount"] = 4
            },
            [new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [64] }]);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(
            manifest,
            new KernelCandidate(new Dictionary<string, int>
            {
                ["HLSLPERF_SCAN_BACKEND"] = 2,
                ["HLSLPERF_GROUP_SIZE"] = 64,
                ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
                ["HLSLPERF_VECTOR_WIDTH"] = 1
            }));

        Assert.Equal(20, plan.Passes.Count);
        Assert.Equal(1, plan.Buffers.Count(buffer => buffer.Name == "radix-scan-0"));
        Assert.Equal("radix-b", plan.VerifiedResource);
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
