using HlslPerf.Core;
using HlslPerf.Rga;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class RgaParserTests
{
    [Fact]
    public void MissingInvalidMetricsAreUnavailableAndGenericOccupancyHasNoAssumedUnit()
    {
        var parsed = RgaStatisticsParser.Parse("p", "SinglePassScan", "resourceUsage.numUsedVgprs = -1\nresourceUsage.scratchMemUsageInBytes = NaN\noccupancy = 75\nresourceUsage.numUsedSgprs = 2.5");
        Assert.Null(parsed.VgprsUsed);
        Assert.Null(parsed.SgprsUsed);
        Assert.Null(parsed.ScratchBytes);
        Assert.Null(parsed.VgprSpills);
        Assert.Null(parsed.SgprSpills);
        Assert.Null(parsed.OccupancyWavesPerSimd);
    }

    [Fact]
    public void ExplicitSpillCountsArePreservedWithoutDerivingFromScratch()
    {
        var parsed = RgaStatisticsParser.Parse("p", "FusedCompactSinglePass", "resourceUsage.scratchMemUsageInBytes = 64\nresourceUsage.numVgprSpills = 3\nresourceUsage.numSgprSpills = 0");
        Assert.Equal(64, parsed.ScratchBytes);
        Assert.Equal(3, parsed.VgprSpills);
        Assert.Equal(0, parsed.SgprSpills);
    }

    [Fact]
    public void ParsesOfficialDx12StatisticsAndLiveVgprSummary()
    {
        const string statistics = """
            Statistics:
                - shaderStageMask                           = 32
                - resourceUsage.numUsedVgprs                = 28
                - resourceUsage.numUsedSgprs                = 18
                - resourceUsage.ldsSizePerLocalWorkGroup    = 65536
                - resourceUsage.ldsUsageSizeInBytes         = 4096
                - resourceUsage.scratchMemUsageInBytes      = 0
                - numPhysicalVgprs                          = 1536
                - numPhysicalSgprs                          = 128
                - numAvailableVgprs                         = 256
                - numAvailableSgprs                         = 106
                - computeWorkGroupSizeX = 256
                - computeWorkGroupSizeY = 1
                - computeWorkGroupSizeZ = 1
            """;
        const string live = "Maximum # VGPR used  21, # VGPR allocated:  32";

        RgaPassAnalysis parsed = RgaStatisticsParser.Parse("pass", "CSMain", statistics, live);

        Assert.Equal(28, parsed.VgprsUsed);
        Assert.Equal(21, parsed.MaximumLiveVgprs);
        Assert.Equal(32, parsed.AllocatedVgprs);
        Assert.Equal(18, parsed.SgprsUsed);
        Assert.Equal(4096, parsed.LdsBytes);
        Assert.Equal(256, parsed.ThreadGroupX);
        Assert.Equal(0.109375, parsed.VgprUsageRatio);
        Assert.Null(parsed.OccupancyWavesPerSimd);
    }

    [Fact]
    public void ParsesCurrentRgaLiveVgprSummary()
    {
        const string statistics = "resourceUsage.numUsedVgprs = 5";
        const string live = "Maximum # VGPR used   5, VGPRs allocated by HW:  12 (5 requested)";

        RgaPassAnalysis parsed = RgaStatisticsParser.Parse("pass", "CSMain", statistics, live);

        Assert.Equal(5, parsed.MaximumLiveVgprs);
        Assert.Equal(12, parsed.AllocatedVgprs);
    }

    [Fact]
    public void CommandUsesDx12LiveDriverOutputsAndRepeatableDefines()
    {
        RgaCollector collector = new(new RgaInstallation("rga.exe", "test"));
        TuningManifest manifest = new()
        {
            SchemaVersion = "2.0",
            Name = "test",
            KernelPath = "kernel.hlsl",
            ShaderModel = "6_0",
            MeasurementBatches = 3,
            Workload = new WorkloadSpec
            {
                Id = "reduction-u32-v1",
                Parameters = new Dictionary<string, long> { ["elementCount"] = 1 }
            },
            Axes = [new CandidateAxis { Name = "GROUP", Values = [64] }]
        };
        KernelCandidate candidate = new(new Dictionary<string, int> { ["GROUP"] = 64, ["EPT"] = 4 });

        IReadOnlyList<string> arguments = collector.BuildArguments(
            manifest, candidate, "ReducePass", "kernel.hlsl", "stats.txt", "isa.txt", "live.txt", "gfx1201");

        Assert.Contains("dx12", arguments);
        Assert.Contains("cs_6_0", arguments);
        Assert.Contains("--analysis", arguments);
        Assert.Contains("--livereg", arguments);
        Assert.Equal(2, arguments.Count(value => value == "--define"));
        Assert.Contains("gfx1201", arguments);
    }
}
