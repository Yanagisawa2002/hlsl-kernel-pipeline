using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HlslPerf.GpuDriven;

namespace HlslPerf.CrowdExport;

public sealed record ExportRequest
{
    public required int SchemaVersion { get; init; }
    public required string Backend { get; init; }
    public required string SeedsFile { get; init; }
    public int Width { get; init; } = 128;
    public int Height { get; init; } = 72;
    public int FramesPerAtlas { get; init; } = 4;
    public int AtlasCount { get; init; } = 2;
    public uint FirstFrame { get; init; }
    public uint VisibilityMask { get; init; } = 15;
    public int Workers { get; init; } = 1;
}

public sealed record ExportedAtlas(uint FirstFrame, string RgbaFile, string RgbaSha256,
    string PreviewFile, string PreviewSha256, int ByteLength);
public sealed record ExportReceipt(int SchemaVersion, string Backend, string RequestSha256,
    string InputSha256, int AgentCount, ExportRequest Request, string ApplicationVersion,
    string ApplicationSha256, string RendererSha256, string Runtime, string OperatingSystem,
    bool GpuExecuted, string PerformanceStatus, IReadOnlyList<ExportedAtlas> Atlases);

/// <summary>File-based asset caller of the existing conventional CPU renderer.
/// No oracle, shader plan, D3D12 device, source checkout or benchmark is needed at runtime.</summary>
public static class CrowdExporter
{
    public const int MaximumInputBytes = 64 * 1024 * 1024;
    public const long RendererBudgetBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static ExportReceipt Export(string requestPath, string outputDirectory)
    {
        string requestFile = Path.GetFullPath(requestPath), output = Path.GetFullPath(outputDirectory);
        byte[] requestBytes = ReadBounded(requestFile, 16 * 1024);
        var request = JsonSerializer.Deserialize<ExportRequest>(requestBytes, Json)
            ?? throw new InvalidDataException("Request is null.");
        Validate(request);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Choose a new output directory; previous exports are preserved.");
        string inputPath = Path.GetFullPath(request.SeedsFile, Path.GetDirectoryName(requestFile)!);
        byte[] input = ReadBounded(inputPath, MaximumInputBytes);
        if (input.Length == 0 || input.Length % 4 != 0)
            throw new InvalidDataException("Seeds must contain one or more complete little-endian uint32 values.");
        // CrowdCpuRenderer uses native uint spans. The file format is always little-endian.
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("This caller requires a little-endian runtime.");
        var scene = new CrowdApplicationScene(input.Length / 4, 0, request.VisibilityMask,
            request.Width, request.Height, request.FramesPerAtlas);
        using var renderer = new CrowdCpuRenderer(scene, input, request.Workers, RendererBudgetBytes);
        Directory.CreateDirectory(output);
        List<ExportedAtlas> atlases = [];
        for (int i = 0; i < request.AtlasCount; i++)
        {
            uint first = request.FirstFrame + (uint)(i * request.FramesPerAtlas);
            _ = renderer.Render(first);
            string rgbaName = $"atlas-{i:D2}.rgba", previewName = $"atlas-{i:D2}.bmp";
            string rgbaHash = WriteNewAndVerify(Path.Combine(output, rgbaName), renderer.Data.Span);
            byte[] preview = EncodePreview(renderer.Data.Span, request.Width, request.Height, request.FramesPerAtlas);
            string previewHash = WriteNewAndVerify(Path.Combine(output, previewName), preview);
            atlases.Add(new(first, rgbaName, rgbaHash, previewName, previewHash, renderer.Data.Length));
        }
        Assembly app = typeof(CrowdExporter).Assembly;
        var receipt = new ExportReceipt(1, "cpu", Hash(requestBytes), Hash(input), scene.AgentCount,
            request, app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Hash(File.ReadAllBytes(app.Location)), Hash(File.ReadAllBytes(typeof(CrowdCpuRenderer).Assembly.Location)),
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, false, "unmeasured", atlases);
        // A receipt is published only after every output was closed and read back byte-for-byte.
        WriteNewAndVerify(Path.Combine(output, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
        return receipt;
    }

    private static void Validate(ExportRequest r)
    {
        if (r.SchemaVersion != 1 || r.Backend != "cpu")
            throw new ArgumentException("Schema 1 and explicit backend 'cpu' are required; this exporter has no GPU fallback.");
        if (string.IsNullOrWhiteSpace(r.SeedsFile) || r.Width is < 1 or > 512 || r.Height is < 1 or > 512 ||
            r.FramesPerAtlas is < 1 or > 12 || r.AtlasCount is < 1 or > 4 ||
            r.Workers < 1 || r.Workers > Math.Min(r.FramesPerAtlas, Environment.ProcessorCount) ||
            (r.VisibilityMask & unchecked(r.VisibilityMask + 1)) != 0 ||
            (ulong)r.FirstFrame + (ulong)(r.FramesPerAtlas * r.AtlasCount) - 1 > 100_000)
            throw new ArgumentException("Invalid bounded scene, frame range, visibility mask or worker count.");
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new InvalidDataException($"Input exceeds {maximumBytes} bytes.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Input changed while reading.");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string WriteNewAndVerify(string path, ReadOnlySpan<byte> bytes)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            throw new IOException("Export readback differs: " + Path.GetFileName(path));
        return Hash(bytes);
    }

    // 32-bit BI_RGB, top-down rows. Each animation frame occupies one horizontal tile.
    public static byte[] EncodePreview(ReadOnlySpan<byte> rgba, int width, int height, int frames)
    {
        if (width is < 1 or > 512 || height is < 1 or > 512 || frames is < 1 or > 12 ||
            rgba.Length != checked(width * height * frames * 4)) throw new ArgumentException("Invalid atlas shape.");
        int stripWidth = width * frames;
        byte[] bmp = new byte[54 + rgba.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        void I32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(at, 4), value);
        I32(2, bmp.Length); I32(10, 54); I32(14, 40); I32(18, stripWidth); I32(22, -height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
        I32(34, rgba.Length);
        for (int frame = 0; frame < frames; frame++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int source = ((frame * height + y) * width + x) * 4;
            int dest = 54 + (y * stripWidth + frame * width + x) * 4;
            bmp[dest] = rgba[source + 2]; bmp[dest + 1] = rgba[source + 1];
            bmp[dest + 2] = rgba[source]; bmp[dest + 3] = 0;
        }
        return bmp;
    }
}
