using System.Runtime.InteropServices;
using HlslPerf.Core;

namespace HlslPerf.Showcase;

internal sealed class ScanParticleWorkload : IKernelWorkload
{
    private const int VisualTileWidth = 16;
    private const int VisualTileHeight = 16;

    private byte[]? flagsData;
    private byte[]? expectedAtlas;
    private string? expectedHash;
    private int cachedElementCount;
    private int cachedWidth;
    private int cachedHeight;
    private int cachedFrameCount;
    private int cachedSeed;

    public string Id => "scan-particle-visual-v1";
    public int ElementCount { get; private set; }
    public int ScanRepeats { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameCount { get; private set; }
    public ReadOnlyMemory<byte> ExpectedAtlas => expectedAtlas
        ?? throw new InvalidOperationException("The workload has not been built yet.");

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload
            ?? throw new InvalidDataException("The visual workload requires a workload specification.");
        ElementCount = spec.GetRequiredInt32("elementCount");
        ScanRepeats = spec.GetRequiredInt32("scanRepeats");
        Width = spec.GetRequiredInt32("width");
        Height = spec.GetRequiredInt32("height");
        FrameCount = spec.GetRequiredInt32("frameCount");
        int seed = spec.GetRequiredInt32("seed");
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int backend = candidate.Defines.TryGetValue("HLSLPERF_SCAN_BACKEND", out int selectedBackend)
            ? selectedBackend
            : 1;
        ValidateCandidate(candidate, groupSize, elementsPerThread);
        EnsureOracle(ElementCount, Width, Height, FrameCount, seed);

        long blockSize = checked((long)groupSize * elementsPerThread);
        List<KernelBufferSpec> buffers = [new("flags", checked(ElementCount * sizeof(uint)), flagsData)];
        List<KernelPassSpec> passes = [];
        if (backend == 3)
        {
            long singlePassBlockSize = checked(blockSize * GetSinglePassItemsScale(candidate));
            uint logicalBlocks = CeilDiv(ElementCount, singlePassBlockSize);
            uint persistentGroups = Math.Min(logicalBlocks, checked((uint)GetPersistentGroupLimit(candidate)));
            int stateBytes = checked(8 + checked((int)logicalBlocks) * 12);
            buffers.Add(new KernelBufferSpec("scan-0", checked(ElementCount * sizeof(uint))));
            buffers.Add(new KernelBufferSpec("single-pass-state", stateBytes, new byte[stateBytes]));
            for (int repeat = 0; repeat < ScanRepeats; ++repeat)
            {
                passes.Add(new KernelPassSpec(
                    $"single-pass-reset-r{repeat}",
                    "ResetSinglePassState",
                    new KernelDispatch(1),
                    null,
                    null,
                    "single-pass-state",
                    null,
                    []));
                passes.Add(new KernelPassSpec(
                    $"single-pass-scan-r{repeat}",
                    "SinglePassScan",
                    new KernelDispatch(persistentGroups),
                    "flags",
                    null,
                    "scan-0",
                    "single-pass-state",
                    [(uint)ElementCount, checked((uint)singlePassBlockSize), logicalBlocks]));
            }
        }
        else
        {
            List<int> levelCounts = [];
            List<uint> levelGroups = [];
            int count = ElementCount;
            int level = 0;
            while (true)
            {
                uint groups = CeilDiv(count, blockSize);
                buffers.Add(new KernelBufferSpec($"scan-{level}", checked(count * sizeof(uint))));
                buffers.Add(new KernelBufferSpec($"sums-{level}", checked((int)groups * sizeof(uint))));
                levelCounts.Add(count);
                levelGroups.Add(groups);
                if (groups == 1)
                    break;
                count = checked((int)groups);
                level++;
            }

            for (int repeat = 0; repeat < ScanRepeats; ++repeat)
            {
                string input = "flags";
                for (int scanLevel = 0; scanLevel < levelCounts.Count; ++scanLevel)
                {
                    KernelDispatch dispatch = DispatchForGroups(levelGroups[scanLevel]);
                    passes.Add(new KernelPassSpec(
                        $"scan-r{repeat}-level-{scanLevel}",
                        "BlockScanPass",
                        dispatch,
                        input,
                        null,
                        $"scan-{scanLevel}",
                        $"sums-{scanLevel}",
                        [(uint)levelCounts[scanLevel], 0, 0, 0, 0, 0, dispatch.X, levelGroups[scanLevel]]));
                    input = $"sums-{scanLevel}";
                }

                for (int childLevel = levelCounts.Count - 2; childLevel >= 0; --childLevel)
                {
                    KernelDispatch dispatch = DispatchForGroups(levelGroups[childLevel]);
                    passes.Add(new KernelPassSpec(
                        $"offset-r{repeat}-level-{childLevel}",
                        "AddScanOffsets",
                        dispatch,
                        $"scan-{childLevel + 1}",
                        null,
                        $"scan-{childLevel}",
                        null,
                        [
                            (uint)levelCounts[childLevel], checked((uint)blockSize), 0, 0, 0, 0,
                            dispatch.X, levelGroups[childLevel]
                        ]));
                }
            }
        }

        int atlasBytes = checked(Width * Height * FrameCount * sizeof(uint));
        buffers.Add(new KernelBufferSpec("frame-atlas", atlasBytes));
        passes.Add(new KernelPassSpec(
            "visualize-scan-particles",
            "VisualizeScan",
            new KernelDispatch(
                CeilDiv(Width, VisualTileWidth),
                CeilDiv(Height, VisualTileHeight),
                checked((uint)FrameCount)),
            "scan-0",
            "flags",
            "frame-atlas",
            null,
            [(uint)ElementCount, 0, (uint)Width, (uint)Height, (uint)FrameCount, (uint)seed]));

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            checked((long)ElementCount * ScanRepeats),
            buffers,
            passes,
            "frame-atlas",
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int width, int height, int frameCount, int seed)
    {
        if (expectedAtlas is not null && cachedElementCount == elementCount && cachedWidth == width &&
            cachedHeight == height && cachedFrameCount == frameCount && cachedSeed == seed)
            return;

        uint[] flags = new uint[elementCount];
        uint[] prefix = new uint[elementCount];
        uint running = 0;
        uint unsignedSeed = unchecked((uint)seed);
        for (int index = 0; index < elementCount; ++index)
        {
            uint flag = (Hash32(unchecked((uint)index) ^ unsignedSeed) & 7) < 5 ? 1u : 0u;
            flags[index] = flag;
            prefix[index] = running;
            running = unchecked(running + flag);
        }

        uint[] atlas = RenderAtlas(prefix, flags, width, height, frameCount, unsignedSeed);
        flagsData = MemoryMarshal.AsBytes(flags.AsSpan()).ToArray();
        expectedAtlas = MemoryMarshal.AsBytes(atlas.AsSpan()).ToArray();
        expectedHash = ContentHash.Sha256(expectedAtlas);
        cachedElementCount = elementCount;
        cachedWidth = width;
        cachedHeight = height;
        cachedFrameCount = frameCount;
        cachedSeed = seed;
    }

    private static uint[] RenderAtlas(
        IReadOnlyList<uint> prefix,
        IReadOnlyList<uint> flags,
        int width,
        int height,
        int frameCount,
        uint seed)
    {
        uint elementCount = checked((uint)prefix.Count);
        uint unsignedWidth = checked((uint)width);
        uint unsignedHeight = checked((uint)height);
        uint stride = Math.Max(1u, elementCount / unsignedWidth);
        uint scale = Math.Max(1u, elementCount / Math.Max(1u, unsignedHeight * 2));
        uint segment = Math.Max(1u, elementCount / 6);
        uint[] atlas = new uint[checked(width * height * frameCount)];

        for (uint frame = 0; frame < frameCount; ++frame)
        {
            for (uint y = 0; y < unsignedHeight; ++y)
            {
                for (uint x = 0; x < unsignedWidth; ++x)
                {
                    uint pixel = y * unsignedWidth + x;
                    uint red = 3;
                    uint green = 7;
                    uint blue = 18;
                    uint star = Hash32(unchecked(pixel + seed));
                    if ((star & 2047) < 5)
                    {
                        uint pulse = 18 + unchecked((star >> 8) + frame * 7) % 36;
                        AddSaturated(ref red, ref green, ref blue, pulse / 2, pulse, pulse);
                    }

                    for (uint trail = 0; trail < 6; ++trail)
                    {
                        uint sample = unchecked(
                            x * stride + trail * segment + frame * unchecked(stride * 3 + 17)) % elementCount;
                        uint center = unchecked(
                            prefix[(int)sample] / scale + trail * Math.Max(1u, unsignedHeight / 6) +
                            frame * (trail + 2) * 3) % unsignedHeight;
                        uint directDistance = y > center ? y - center : center - y;
                        uint distance = Math.Min(directDistance, unsignedHeight - directDistance);
                        uint radius = 2 + (Hash32(sample ^ seed ^ unchecked(trail * 0x9e3779b9u)) & 1);

                        if (distance <= radius)
                        {
                            uint power = (radius + 1 - distance) * 54;
                            switch (trail % 3)
                            {
                                case 0:
                                    AddSaturated(ref red, ref green, ref blue, power / 5, power, power);
                                    break;
                                case 1:
                                    AddSaturated(ref red, ref green, ref blue, power, power / 4, power);
                                    break;
                                default:
                                    AddSaturated(ref red, ref green, ref blue, power, power / 2, power / 8);
                                    break;
                            }
                        }

                        if (flags[(int)sample] != 0 && distance < 10 &&
                            (Hash32(unchecked(sample + frame * 131 + seed)) & 63) < 2)
                            AddSaturated(ref red, ref green, ref blue, 42, 52, 68);
                    }

                    int atlasIndex = checked((int)(frame * unsignedWidth * unsignedHeight + pixel));
                    atlas[atlasIndex] = red | (green << 8) | (blue << 16) | 0xff000000;
                }
            }
        }
        return atlas;
    }

    private static void AddSaturated(
        ref uint red,
        ref uint green,
        ref uint blue,
        uint addRed,
        uint addGreen,
        uint addBlue)
    {
        red = Math.Min(255u, red + addRed);
        green = Math.Min(255u, green + addGreen);
        blue = Math.Min(255u, blue + addBlue);
    }

    private static uint Hash32(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7feb352du);
        value ^= value >> 15;
        value = unchecked(value * 0x846ca68bu);
        value ^= value >> 16;
        return value;
    }

    private static uint CeilDiv(int value, long divisor) => checked((uint)((value + divisor - 1) / divisor));

    private static KernelDispatch DispatchForGroups(uint groupCount)
    {
        uint x = Math.Min(65_535u, groupCount);
        uint y = (groupCount + x - 1) / x;
        return new KernelDispatch(x, y);
    }

    private static void ValidateCandidate(KernelCandidate candidate, int groupSize, int elementsPerThread)
    {
        if (groupSize is <= 0 or > 1024 || (groupSize & (groupSize - 1)) != 0)
            throw new InvalidDataException($"Candidate '{candidate.Id}' group size must be a power of two in 1..1024.");
        if (elementsPerThread is <= 0 or > 16)
            throw new InvalidDataException($"Candidate '{candidate.Id}' elements per thread must be in 1..16.");
    }

    private static int GetPersistentGroupLimit(KernelCandidate candidate)
    {
        int limit = candidate.Defines.TryGetValue("HLSLPERF_SINGLE_PASS_GROUPS", out int value) ? value : 256;
        if (limit is <= 0 or > 65_535)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' persistent group limit must be in 1..65,535.");
        return limit;
    }

    private static int GetSinglePassItemsScale(KernelCandidate candidate)
    {
        int scale = candidate.Defines.TryGetValue("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", out int value) ? value : 1;
        if (scale is <= 0 or > 64)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' single-pass items scale must be in 1..64.");
        return scale;
    }
}
