using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

// Plan/oracle construction only: this test project has no D3D12 executor reference.
public sealed class WaveTiledScanTests
{
    [Theory]
    [InlineData(false, 32, 1)]
    [InlineData(false, 64, 4095)]
    [InlineData(false, 32, 4096)]
    [InlineData(false, 64, 4097)]
    [InlineData(false, 32, 8193)]
    [InlineData(true, 32, 1)]
    [InlineData(true, 64, 4095)]
    [InlineData(true, 32, 4096)]
    [InlineData(true, 64, 4097)]
    [InlineData(true, 32, 8193)]
    public void OptInKeepsFullOutputContractAndBindsCompleteReset(bool compaction, int wave, int count)
    {
        TuningManifest manifest = Manifest(compaction, count);
        IKernelWorkload workload = BuiltinWorkloads.Resolve(manifest);
        KernelCandidate candidate = WaveTiledScanCandidates.Create(wave);
        KernelExecutionPlan plan = workload.Build(manifest, candidate);
        KernelExecutionPlan legacy = workload.Build(manifest, With(candidate, WaveTiledScanCandidates.OptInDefine, 0));
        uint blocks = (uint)((count + 4095) / 4096);
        Assert.Equal(KernelAbiV1.Id, plan.AbiVersion);
        Assert.Equal(legacy.ExpectedSha256, plan.ExpectedSha256);
        Assert.Equal(legacy.Buffers.Select(b => (b.Name, b.ByteLength)), plan.Buffers.Select(b => (b.Name, b.ByteLength)));
        Assert.Equal(legacy.Buffers[0].InitialData, plan.Buffers[0].InitialData);
        Assert.Equal("Unmeasured", WaveTiledScanCandidates.PerformanceStatus);
        Assert.Collection(plan.Passes,
            reset =>
            {
                Assert.Equal(WaveTiledScanCandidates.ResetEntryPoint, reset.EntryPoint);
                Assert.Equal(new KernelDispatch(1), reset.Dispatch);
                Assert.Equal(new uint[] { 0, 0, blocks }, reset.Constants);
                Assert.Equal(compaction ? "compaction-state" : "single-pass-state", reset.Output0);
                Assert.Null(reset.Input0);
                Assert.Null(reset.Output1);
            },
            scan =>
            {
                Assert.Equal(compaction ? WaveTiledScanCandidates.CompactionEntryPoint : WaveTiledScanCandidates.ScanEntryPoint, scan.EntryPoint);
                Assert.Equal(new KernelDispatch(Math.Min(blocks, 256)), scan.Dispatch);
                Assert.Equal((uint)count, scan.Constants[0]);
                Assert.Equal(4096u, scan.Constants[1]);
                Assert.Equal(blocks, scan.Constants[2]);
                Assert.Equal("input", scan.Input0);
                Assert.Equal(compaction ? "compaction-state" : "single-pass-state", scan.Output1);
                if (compaction) Assert.Equal(7u, scan.Constants[3]);
            });
        Assert.Equal("ResetSinglePassState", legacy.Passes[0].EntryPoint);
        Assert.Equal(compaction ? "FusedCompactSinglePass" : "SinglePassScan", legacy.Passes[1].EntryPoint);
        Assert.Empty(legacy.Passes[0].Constants);
    }

    [Theory]
    [InlineData("HLSLPERF_SCAN_WAVE_TILED", 2)]
    [InlineData("HLSLPERF_SCAN_BACKEND", 2)]
    [InlineData("HLSLPERF_SCAN_OPERATOR", 2)]
    [InlineData("HLSLPERF_VECTOR_WIDTH", 1)]
    [InlineData("HLSLPERF_WAVE_SIZE", 0)]
    [InlineData("HLSLPERF_GROUP_SIZE", 16)]
    [InlineData("HLSLPERF_GROUP_SIZE", 96)]
    [InlineData("HLSLPERF_GROUP_SIZE", 2048)]
    [InlineData("HLSLPERF_ELEMENTS_PER_THREAD", 0)]
    [InlineData("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 17)]
    [InlineData("HLSLPERF_WAVE_TILED_MAX_POLLS", 0)]
    [InlineData("HLSLPERF_WAVE_TILED_MAX_POLLS", 17)]
    [InlineData("HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS", 1)]
    public void UnsupportedConfigurationsAreRejectedBeforeDispatch(string name, int value)
    {
        foreach (bool compaction in new[] { false, true })
        {
            TuningManifest manifest = Manifest(compaction, 5);
            Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(
                manifest, With(WaveTiledScanCandidates.Create(), name, value)));
        }
    }

    [Fact]
    public void NonMultipleOfFourAndUnfixedWaveAreRejected()
    {
        TuningManifest manifest = Manifest(false, 5);
        KernelCandidate candidate = With(With(WaveTiledScanCandidates.Create(), "HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 1),
            "HLSLPERF_ELEMENTS_PER_THREAD", 6);
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(manifest, candidate));
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(manifest, WaveTiledScanCandidates.Create(16)));
    }

    [Fact]
    public void FixedWaveRequiresSupportedShaderModel()
    {
        TuningManifest manifest = Manifest(false, 5, "6_0");
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(manifest, WaveTiledScanCandidates.Create()));
    }

    [Fact]
    public void SmallerPersistentPoolDoesNotTruncateLogicalResetRange()
    {
        TuningManifest manifest = Manifest(false, 5 * 4096 + 3);
        KernelExecutionPlan plan = BuiltinWorkloads.Resolve(manifest).Build(manifest,
            With(WaveTiledScanCandidates.Create(), "HLSLPERF_SINGLE_PASS_GROUPS", 1));
        Assert.Equal(6u, plan.Passes[0].Constants[2]);
        Assert.Equal(6u, plan.Passes[1].Constants[2]);
        Assert.Equal(1u, plan.Passes[1].Dispatch.X);
        Assert.Equal(8 + 6 * 12, plan.Buffers.Single(b => b.Name == "single-pass-state").ByteLength);
    }

    private static KernelCandidate With(KernelCandidate original, string name, int value) =>
        new(new Dictionary<string, int>(original.Defines) { [name] = value });

    private static TuningManifest Manifest(bool compaction, int count, string model = "6_6") => new()
    {
        SchemaVersion = "2.0",
        Name = "wave-tiled-cpu-contract",
        KernelPath = compaction ? "kernels/compaction.hlsl" : "kernels/scan.hlsl",
        ShaderModel = model,
        Workload = new WorkloadSpec
        {
            Id = compaction ? "stream-compaction-u32-v1" : "exclusive-scan-u32-v1",
            Parameters = new Dictionary<string, long> { ["elementCount"] = count, ["seed"] = 19, ["predicateMask"] = 7 }
        },
        Axes = [new CandidateAxis { Name = "HLSLPERF_GROUP_SIZE", Values = [256] }]
    };
}
