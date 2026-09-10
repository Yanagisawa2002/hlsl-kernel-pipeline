using System.Globalization;
using System.Text.Json;
using HlslPerf.Workloads;

// A host contract exporter that never allocates records or scratch. It references
// only Workloads/Core and cannot create a device, dispatch, tune, or measure an arm.
int count = 4097, bits = 32, radix = 8;
bool pairs = false, nativeBuffers = false, packedOutput = false;
string? output = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--count": count = ReadInt(); break;
        case "--bits": bits = ReadInt(); break;
        case "--radix-bits": radix = ReadInt(); break;
        case "--pairs": pairs = true; break;
        case "--native-buffers": nativeBuffers = true; break;
        case "--packed-output": packedOutput = true; break;
        case "--output": output = Read(); break;
        default: throw new ArgumentException($"Unknown option '{args[i]}'. This program exports a plan only.");
    }
    string Read() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
    int ReadInt() => int.Parse(Read(), CultureInfo.InvariantCulture);
}
RadixTileLayout layout = nativeBuffers
    ? RadixTileLayout.CreateForNativeBuffers(count, bits, radix, pairs)
    : RadixTileLayout.Create(count, bits, radix, pairs);
string json = JsonSerializer.Serialize(new
{
    schema = "hlslperf.radix-tile-plan.v1", status = RadixTileLayout.PerformanceStatus,
    gpuExecuted = false, nativeBuffers,
    kernelPath = "kernels/radix-sort.hlsl",
    inputFormat = pairs ? "interleaved-uint32-key-payload" : "uint32-key",
    outputFormat = pairs && !packedOutput ? "separate-uint32-keys-payloads" : "same-as-input",
    optionalSoaInputPackingEntryPoint = pairs ? "PackRadixPairs" : null,
    constants = new[] { "elementCount", "elementsPerBlock", "radixShift", "digitMask",
        "prefixTiles", "recordTiles", "dispatchGroupsX", "dispatchGroupCount" },
    layout, plan = layout.DescribePlan(pairs && !packedOutput)
}, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
if (output is null) Console.WriteLine(json);
else
{
    string path = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, json + Environment.NewLine);
}
