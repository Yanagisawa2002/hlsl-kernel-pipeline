using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.CrowdExport;
using HlslPerf.GpuDriven;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class CrowdExporterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hlsl-export-" + Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(folder, "output");

    private string Request(byte[]? input = null, string? json = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "seeds.u32"), input ?? CrowdApplication.GenerateAgents(31, 19088743));
        string request = Path.Combine(folder, "request.json");
        File.WriteAllText(request, json ?? """
            {"schemaVersion":1,"backend":"cpu","seedsFile":"seeds.u32","width":64,"height":32,
             "framesPerAtlas":3,"atlasCount":2,"firstFrame":0,"visibilityMask":0,"workers":1}
            """);
        return request;
    }

    [Fact]
    public void FileCallerExportsTwoWindowsMatchingIndependentPixelOracle()
    {
        string request = Request();
        var receipt = CrowdExporter.Export(request, Output);
        byte[] oracle = Oracle();
        Assert.Equal("cpu", receipt.Backend);
        Assert.False(receipt.GpuExecuted);
        Assert.Equal("unmeasured", receipt.PerformanceStatus);
        Assert.Equal(31, receipt.AgentCount);
        Assert.Equal(Hash(File.ReadAllBytes(Path.Combine(folder, "seeds.u32"))), receipt.InputSha256);
        Assert.Equal(Hash(File.ReadAllBytes(request)), receipt.RequestSha256);
        Assert.Equal(2, receipt.Atlases.Count);
        for (int i = 0; i < receipt.Atlases.Count; i++)
        {
            var entry = receipt.Atlases[i];
            byte[] raw = File.ReadAllBytes(Path.Combine(Output, entry.RgbaFile));
            Assert.Equal((uint)(i * 3), entry.FirstFrame);
            Assert.Equal(oracle.AsSpan(i * raw.Length, raw.Length).ToArray(), raw);
            Assert.Equal(Hash(raw), entry.RgbaSha256);
            Assert.Equal(Hash(File.ReadAllBytes(Path.Combine(Output, entry.PreviewFile))), entry.PreviewSha256);
        }
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "receipt.json")));
        Assert.Equal(receipt.InputSha256, saved.RootElement.GetProperty("inputSha256").GetString());
        Assert.Equal(2, saved.RootElement.GetProperty("atlases").GetArrayLength());
    }

    [Fact]
    public void PreviewPreservesFrameOrderTopDownRowsAndRgbChannels()
    {
        // Two 1x2 frames with distinct top/bottom pixels; input is frame-major RGBA.
        byte[] raw = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        byte[] bmp = CrowdExporter.EncodePreview(raw, 1, 2, 2);
        Assert.Equal((byte)'B', bmp[0]); Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
        Assert.Equal(new byte[] { 3, 2, 1, 0, 9, 8, 7, 0, 6, 5, 4, 0, 12, 11, 10, 0 }, bmp[54..]);
        Assert.Throws<ArgumentException>(() => CrowdExporter.EncodePreview(raw, 2, 2, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void TruncatedOrEmptySeedFileCannotCreateSuccessReceipt(int bytes)
    {
        string request = Request(new byte[bytes]);
        Assert.Throws<InvalidDataException>(() => CrowdExporter.Export(request, Output));
        Assert.False(Directory.Exists(Output));
    }

    [Theory]
    [InlineData("\"backend\":\"cpu\"", "\"backend\":\"d3d12\"")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"firstFrame\":0", "\"firstFrame\":99999")]
    [InlineData("\"visibilityMask\":0", "\"visibilityMask\":5")]
    public void UnsupportedRequestsFailWithoutImplicitSubstitution(string from, string to)
    {
        string request = Request();
        File.WriteAllText(request, File.ReadAllText(request).Replace(from, to, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => CrowdExporter.Export(request, Output));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void UnknownSettingsAndExistingOutputAreRejected()
    {
        string request = Request();
        string original = File.ReadAllText(request);
        File.WriteAllText(request, original.Replace("\"workers\":1", "\"workers\":1,\"widht\":64"));
        Assert.Throws<JsonException>(() => CrowdExporter.Export(request, Output));
        File.WriteAllText(request, original);
        Directory.CreateDirectory(Output);
        string marker = Path.Combine(Output, "keep.txt");
        File.WriteAllText(marker, "preserve previous export");
        Assert.Throws<IOException>(() => CrowdExporter.Export(request, Output));
        Assert.Equal("preserve previous export", File.ReadAllText(marker));
        Assert.False(File.Exists(Path.Combine(Output, "receipt.json")));
    }

    private static byte[] Oracle()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HlslKernelPipeline.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Repository root not found.");
        TuningManifest manifest = new()
        {
            Name = "export-caller-independent-oracle", KernelPath = Path.Combine(root.FullName, "gpu-driven-demo/crowd-vfx.hlsl"),
            ShaderModel = "6_6", Axes = [], Workload = new() { Id = "crowd-vfx-gpu-driven-v1", Parameters = new Dictionary<string, long>
            { ["agentCount"] = 31, ["seed"] = 19088743, ["visibilityMask"] = 0,
              ["width"] = 64, ["height"] = 32, ["frameCount"] = 6, ["tileSize"] = 16 } }
        };
        var candidate = new KernelCandidate(new Dictionary<string, int>
        { ["HLSLPERF_CROWD_BACKEND"] = 1, ["HLSLPERF_GROUP_SIZE"] = 256,
          ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4, ["HLSLPERF_VECTOR_WIDTH"] = 1,
          ["HLSLPERF_CROWD_TILE_SIZE"] = 16, ["HLSLPERF_CROWD_TILE_BINS"] = 8 });
        var oracle = new CrowdVfxWorkload(); oracle.Build(manifest, candidate); return oracle.ExpectedAtlas.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
}
