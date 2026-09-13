using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace HlslPerf.Core.Tests;

public sealed class PrimitiveOperationTests
{
    private static string Root
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("HLSLPERF_TEST_REPOSITORY_ROOT");
            if (configured is not null) return configured;
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "HlslKernelPipeline.slnx"))) path = path.Parent;
            return path?.FullName ?? throw new InvalidDataException("Repository root unavailable.");
        }
    }
    private static OperationRuntime Runtime => new("test-device", "test-driver", "D3D12", "6_7", new string('a', 64), 32, 64);

    [Fact]
    public void ExternalSortPreservesArbitraryPayloadsAndStableFullWidthOracle()
    {
        var plan = PrimitiveOperations.StableSort(Root, [uint.MaxValue, 0, uint.MaxValue, 0],
            [91, uint.MaxValue, 17, 8], SortImplementation.AmdParallelSort);
        Assert.Equal(Hash([0, 0, uint.MaxValue, uint.MaxValue]), plan.Outputs[0].ExpectedSha256);
        Assert.Equal(Hash([uint.MaxValue, 8, 91, 17]), plan.Outputs[1].ExpectedSha256);
        Assert.Equal(UnifiedStage.InputRestore, plan.Passes[0].Stage);
        Assert.Equal(40, plan.Passes.Count(p => p.Stage == UnifiedStage.Algorithm));
    }

    private static string Hash(uint[] values) => ContentHash.Sha256(MemoryMarshal.AsBytes(values.AsSpan()).ToArray());

    [Fact]
    public void DefaultsRemainInternalAndUnconfirmedRequestsFallBack()
    {
        var fallback = PrimitiveOperations.ExclusiveScan(Root, [1, uint.MaxValue, 8]);
        var external = PrimitiveOperations.ExclusiveScan(Root, [1, uint.MaxValue, 8], ScanImplementation.GpuPrefixSumsReduceThenScan);
        Assert.Equal("internal-scan-baseline", fallback.Implementation);
        var selected = PrimitiveOperations.Select(Root, external, fallback, Runtime);
        Assert.True(selected.UsedFallback);
        Assert.Equal(OperationPerformanceStatus.Unmeasured, selected.PerformanceStatus);
        Assert.False(PrimitiveOperations.Select(Root, external, fallback, Runtime, allowUnmeasured: true).UsedFallback);
        Assert.True(PrimitiveOperations.Select(Root, external, fallback, Runtime with { ShaderModel = "6_6" }, allowUnmeasured: true).UsedFallback);
    }

    [Fact]
    public void ProfileMustMatchTrustedConfirmationSourcePlanAbiAndDeviceDriver()
    {
        var fallback = PrimitiveOperations.ExclusiveScan(Root, [1, 2]);
        var external = PrimitiveOperations.ExclusiveScan(Root, [1, 2], ScanImplementation.GpuPrefixSumsReduceThenScan);
        var hash = OperationIdentity.Compute(external, Root);
        string confirmation = new('b', 64);
        var profile = new OperationDeploymentProfile(external.Implementation, external.SemanticId,
            UnifiedOperationPlan.AbiId, hash, Runtime, confirmation, OperationPerformanceStatus.Confirmed);
        Assert.False(PrimitiveOperations.Select(Root, external, fallback, Runtime, profile, trustedConfirmationSha256: confirmation).UsedFallback);
        Assert.True(PrimitiveOperations.Select(Root, external, fallback, Runtime, profile).UsedFallback);
        foreach (var invalid in new[] { profile with { Abi = KernelAbiV1.Id }, profile with { SourceAndPlanSha256 = new string('c', 64) },
            profile with { Runtime = Runtime with { Driver = "different" } }, profile with { PerformanceStatus = OperationPerformanceStatus.Unmeasured } })
            Assert.True(PrimitiveOperations.Select(Root, external, fallback, Runtime, invalid, true, confirmation).UsedFallback);
        var differentInput = PrimitiveOperations.ExclusiveScan(Root, [1, 3]);
        Assert.Throws<InvalidDataException>(() => PrimitiveOperations.Select(Root, external, differentInput, Runtime));
    }

    [Fact]
    public void EmptyInputAndPayloadsHaveDeterministicSentinelsAndNoDispatch()
    {
        var plan = PrimitiveOperations.StableSort(Root, [], [], SortImplementation.AmdParallelSort);
        Assert.Empty(plan.Shaders);
        Assert.Equal(2, plan.Outputs.Count);
        Assert.All(plan.Outputs, o => Assert.Equal(ContentHash.Sha256(new byte[4]), o.ExpectedSha256));
        Assert.Throws<ArgumentException>(() => PrimitiveOperations.StableSort(Root, [1], []));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("json")]
    [InlineData("missing-sources")]
    [InlineData("sources-object")]
    [InlineData("source-missing")]
    [InlineData("source-duplicate")]
    [InlineData("missing-files")]
    [InlineData("files-object")]
    [InlineData("empty-files")]
    [InlineData("null-path")]
    public void UnavailableOrInvalidPinnedSourceFallsBack(string failure)
    {
        // Isolate faults in a new tiny fixture; never change pinned checkout assets.
        var fixture = Directory.CreateTempSubdirectory("hlslperf-source-fallback-");
        try
        {
            foreach (string source in Directory.EnumerateFiles(Path.Combine(Root, "kernels"), "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(fixture.FullName, Path.GetRelativePath(Root, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            string lockPath = Path.Combine(fixture.FullName, "third_party/upstream-lock.json");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            var sourceLock = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "third_party/upstream-lock.json")))!;
            var pinned = sourceLock["sources"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "gpu-prefix-sums")!;
            if (failure == "revision") pinned["commit"] = new string('0', 40);
            switch (failure)
            {
                case "missing-sources": sourceLock.AsObject().Remove("sources"); break;
                case "sources-object": sourceLock["sources"] = new JsonObject(); break;
                case "source-missing": sourceLock["sources"] = new JsonArray(); break;
                case "source-duplicate": sourceLock["sources"]!.AsArray().Add(pinned.DeepClone()); break;
                case "missing-files": pinned.AsObject().Remove("files"); break;
                case "files-object": pinned["files"] = new JsonObject(); break;
                case "empty-files": pinned["files"] = new JsonArray(); break;
                case "null-path": pinned["files"]![0]!["localPath"] = null; break;
            }
            if (failure == "bytes")
            {
                string relative = pinned["files"]![0]!["localPath"]!.GetValue<string>();
                string target = Path.Combine(fixture.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, "Deliberately corrupt CPU-test source fixture.");
            }
            File.WriteAllText(lockPath, failure == "json" ? "{" : sourceLock.ToJsonString());
            if (failure is not ("revision" or "bytes" or "missing" or "json"))
                Assert.Throws<InvalidDataException>(() =>
                    PrimitiveOperations.VerifyPinnedSource(fixture.FullName, "gps-reduce-then-scan"));
            var fallback = PrimitiveOperations.ExclusiveScan(fixture.FullName, [3, 0, 2]);
            var external = PrimitiveOperations.ExclusiveScan(fixture.FullName, [3, 0, 2], ScanImplementation.GpuPrefixSumsReduceThenScan);
            var selected = PrimitiveOperations.Select(fixture.FullName, external, fallback, Runtime, allowUnmeasured: true);
            Assert.True(selected.UsedFallback);
            Assert.Same(fallback, selected.Plan);
            Assert.Equal(OperationPerformanceStatus.Unmeasured, selected.PerformanceStatus);
            Assert.Equal(OperationIdentity.Compute(fallback, fixture.FullName), selected.SourceAndPlanSha256);
            Assert.Contains("shader assets", selected.Reason);
        }
        finally { fixture.Delete(recursive: true); } // Only this test's newly created directory.
    }
}
