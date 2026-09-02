using System.Numerics;
using System.Runtime.InteropServices;
using HlslPerf.Core;

namespace HlslPerf.GpuDriven;

/// <summary>
/// Complete application workload: visibility classification, stream
/// compaction, screen-tile histogram/offsets, bin scatter, and deterministic
/// compute rasterization into a multi-frame RGBA atlas.
/// </summary>
public sealed class CrowdVfxWorkload : IKernelWorkload
{
    private OracleCache? oracle;

    public string Id => CrowdVfxWorkloadProvider.CrowdVfxWorkloadId;

    public CrowdVfxSceneInfo Scene { get; private set; } = CrowdVfxSceneInfo.Empty;

    public ReadOnlyMemory<byte> ExpectedAtlas => oracle?.Atlas
        ?? throw new InvalidOperationException("The workload must be built before reading its oracle atlas.");

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload
            ?? throw new InvalidDataException("The Crowd/VFX workload requires a workload specification.");
        int agentCount = spec.GetRequiredInt32("agentCount");
        int width = spec.GetRequiredInt32("width");
        int height = spec.GetRequiredInt32("height");
        int frameCount = spec.GetRequiredInt32("frameCount");
        int seed = spec.GetRequiredInt32("seed");
        uint visibilityMask = checked((uint)spec.GetRequiredInt32("visibilityMask"));
        int tileSize = spec.GetInt32("tileSize", 16);
        ValidateScene(agentCount, width, height, frameCount, visibilityMask, tileSize);

        int backend = candidate.GetRequired("HLSLPERF_CROWD_BACKEND");
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int waveSize = OptionalDefine(candidate, "HLSLPERF_WAVE_SIZE", 0);
        int tileCountX = checked((width + tileSize - 1) / tileSize);
        int tileCountY = checked((height + tileSize - 1) / tileSize);
        int tileCount = checked(tileCountX * tileCountY);
        int paddedTileCount = checked((int)BitOperations.RoundUpToPowerOf2((uint)tileCount));
        int definedTileBins = candidate.GetRequired("HLSLPERF_CROWD_TILE_BINS");
        int definedTileSize = candidate.GetRequired("HLSLPERF_CROWD_TILE_SIZE");
        ValidateCandidate(
            manifest,
            candidate,
            backend,
            groupSize,
            elementsPerThread,
            vectorWidth,
            waveSize,
            tileSize,
            paddedTileCount,
            definedTileSize,
            definedTileBins);

        OracleCache data = EnsureOracle(
            agentCount,
            width,
            height,
            frameCount,
            seed,
            visibilityMask,
            tileSize,
            tileCountX,
            tileCountY,
            paddedTileCount);
        Scene = new CrowdVfxSceneInfo(
            agentCount,
            width,
            height,
            frameCount,
            tileSize,
            tileCount,
            paddedTileCount,
            visibilityMask,
            data.VisibleCounts,
            data.AtlasHash);

        int maximumVisibleCount = Math.Max(1, data.VisibleCounts.Max());
        int atlasByteLength = checked(width * height * frameCount * sizeof(uint));
        List<KernelBufferSpec> buffers =
        [
            new("agents", checked(agentCount * sizeof(uint)), data.Agents),
            new("visible-seeds", checked((maximumVisibleCount + 1) * sizeof(uint))),
            new("tile-counts", checked(paddedTileCount * sizeof(uint))),
            new("tile-offsets", checked((paddedTileCount + 1) * sizeof(uint))),
            new("tile-cursors", checked(paddedTileCount * sizeof(uint))),
            new("tile-seeds", checked(maximumVisibleCount * sizeof(uint))),
            new("frame-atlas", atlasByteLength)
        ];
        List<KernelPassSpec> passes = [];

        long baseBlockSize = checked((long)groupSize * elementsPerThread);
        ScanScratch? scan = null;
        uint classificationGroups = CeilDiv(agentCount, baseBlockSize);
        KernelDispatch classificationDispatch = DispatchForGroups(classificationGroups);
        uint fusedLogicalBlocks = 0;
        uint fusedPersistentGroups = 0;
        long fusedBlockSize = 0;
        if (backend == 1)
        {
            buffers.Add(new KernelBufferSpec("visibility-flags", checked(agentCount * sizeof(uint))));
            scan = ScanScratch.Declare(buffers, "crowd", agentCount, baseBlockSize);
        }
        else
        {
            int scale = OptionalDefine(candidate, "HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 1);
            int persistentLimit = OptionalDefine(candidate, "HLSLPERF_SINGLE_PASS_GROUPS", 256);
            fusedBlockSize = checked(baseBlockSize * scale);
            fusedLogicalBlocks = CeilDiv(agentCount, fusedBlockSize);
            fusedPersistentGroups = Math.Min(fusedLogicalBlocks, checked((uint)persistentLimit));
            int stateBytes = checked(8 + checked((int)fusedLogicalBlocks) * 12);
            buffers.Add(new KernelBufferSpec("crowd-compaction-state", stateBytes, new byte[stateBytes]));
        }

        for (int frame = 0; frame < frameCount; ++frame)
        {
            uint visibleCount = checked((uint)data.VisibleCounts[frame]);
            if (backend == 1)
            {
                passes.Add(new KernelPassSpec(
                    $"classify-f{frame}",
                    "ProduceCrowdVisibilityFlags",
                    classificationDispatch,
                    "agents",
                    null,
                    "visibility-flags",
                    null,
                    [
                        (uint)agentCount, checked((uint)baseBlockSize), 0, (uint)width,
                        (uint)frame, visibilityMask, classificationDispatch.X, classificationGroups
                    ]));
                scan!.AppendPasses(passes, "visibility-flags", $"visibility-f{frame}");
                passes.Add(new KernelPassSpec(
                    $"scatter-visible-f{frame}",
                    "ScatterVisibleCrowdSeeds",
                    classificationDispatch,
                    "agents",
                    scan.Output,
                    "visible-seeds",
                    null,
                    [
                        (uint)agentCount, checked((uint)baseBlockSize), 0, (uint)width,
                        (uint)frame, visibilityMask, classificationDispatch.X, classificationGroups
                    ]));
            }
            else
            {
                passes.Add(new KernelPassSpec(
                    $"fused-reset-f{frame}",
                    "ResetSinglePassState",
                    new KernelDispatch(1),
                    null,
                    null,
                    "crowd-compaction-state",
                    null,
                    []));
                passes.Add(new KernelPassSpec(
                    $"fused-compact-f{frame}",
                    "FusedCrowdVisibilityCompact",
                    new KernelDispatch(fusedPersistentGroups),
                    "agents",
                    null,
                    "visible-seeds",
                    "crowd-compaction-state",
                    [
                        (uint)agentCount, checked((uint)fusedBlockSize), fusedLogicalBlocks, (uint)width,
                        (uint)frame, visibilityMask
                    ]));
            }

            passes.Add(new KernelPassSpec(
                $"reset-tile-bins-f{frame}",
                "ResetCrowdTileBins",
                new KernelDispatch(CeilDiv(paddedTileCount, 256)),
                null,
                null,
                "tile-counts",
                "tile-cursors",
                [(uint)paddedTileCount]));

            uint tileWorkGroups = Math.Max(1, CeilDiv(data.VisibleCounts[frame], baseBlockSize));
            KernelDispatch tileWorkDispatch = DispatchForGroups(tileWorkGroups);
            IReadOnlyList<uint> tileConstants =
            [
                visibleCount, checked((uint)baseBlockSize), 0, (uint)width,
                (uint)height, (uint)frame, tileWorkDispatch.X, tileWorkGroups
            ];
            passes.Add(new KernelPassSpec(
                $"histogram-tiles-f{frame}",
                "BuildCrowdTileHistogram",
                tileWorkDispatch,
                "visible-seeds",
                null,
                "tile-counts",
                null,
                tileConstants));
            passes.Add(new KernelPassSpec(
                $"prefix-tile-offsets-f{frame}",
                "PrefixCrowdTileHistogram",
                new KernelDispatch(1),
                "tile-counts",
                null,
                "tile-offsets",
                null,
                [(uint)paddedTileCount]));
            passes.Add(new KernelPassSpec(
                $"scatter-tile-seeds-f{frame}",
                "ScatterCrowdTileSeeds",
                tileWorkDispatch,
                "visible-seeds",
                "tile-offsets",
                "tile-seeds",
                "tile-cursors",
                tileConstants));
            passes.Add(new KernelPassSpec(
                $"rasterize-crowd-vfx-f{frame}",
                "RasterizeCrowdVfx",
                new KernelDispatch(CeilDiv(width, 8), CeilDiv(height, 8)),
                "tile-seeds",
                "tile-offsets",
                "frame-atlas",
                null,
                [
                    visibleCount, 0, 0, (uint)width, (uint)height, (uint)frame,
                    (uint)tileCountX, (uint)tileCountY
                ]));
        }

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            checked((long)agentCount * frameCount),
            buffers,
            passes,
            "frame-atlas",
            data.AtlasHash);
        plan.Validate();
        return plan;
    }

    private OracleCache EnsureOracle(
        int agentCount,
        int width,
        int height,
        int frameCount,
        int seed,
        uint visibilityMask,
        int tileSize,
        int tileCountX,
        int tileCountY,
        int paddedTileCount)
    {
        OracleKey key = new(agentCount, width, height, frameCount, seed, visibilityMask, tileSize);
        if (oracle?.Key == key)
            return oracle;

        uint[] agents = GenerateAgents(agentCount, seed);
        uint[] atlas = new uint[checked(width * height * frameCount)];
        int[] visibleCounts = new int[frameCount];
        for (int frame = 0; frame < frameCount; ++frame)
        {
            InitializeBackground(atlas, width, height, frame);
            foreach (uint agentSeed in agents)
            {
                if (!IsVisible(agentSeed, frame, width, visibilityMask))
                    continue;
                visibleCounts[frame]++;
                (int x, int y) = Position(agentSeed, frame, width, height);
                Splat(atlas, frame, width, height, x, y, agentSeed);
            }
        }

        byte[] agentBytes = MemoryMarshal.AsBytes(agents.AsSpan()).ToArray();
        byte[] atlasBytes = MemoryMarshal.AsBytes(atlas.AsSpan()).ToArray();
        oracle = new OracleCache(
            key,
            agentBytes,
            atlasBytes,
            ContentHash.Sha256(atlasBytes),
            visibleCounts,
            tileCountX,
            tileCountY,
            paddedTileCount);
        return oracle;
    }

    private static void ValidateScene(
        int agentCount,
        int width,
        int height,
        int frameCount,
        uint visibilityMask,
        int tileSize)
    {
        if (agentCount <= 0 || width <= 0 || height <= 0 || frameCount <= 0 || tileSize <= 0)
            throw new InvalidDataException("Crowd/VFX scene dimensions, frame count, tile size, and agent count must be positive.");
        if (checked((long)width * height * frameCount * sizeof(uint)) > int.MaxValue)
            throw new InvalidDataException("Crowd/VFX atlas exceeds the ABI v1 buffer-size limit.");
        if ((visibilityMask & (visibilityMask + 1)) != 0)
            throw new InvalidDataException("visibilityMask must be a contiguous low-bit mask such as 3, 7, 15, or 31.");
        int tiles = checked(((width + tileSize - 1) / tileSize) * ((height + tileSize - 1) / tileSize));
        if (tiles > 1024)
            throw new InvalidDataException("Crowd/VFX tile grid must fit in a single prefix-scan thread group (at most 1024 tiles).");
    }

    private static void ValidateCandidate(
        TuningManifest manifest,
        KernelCandidate candidate,
        int backend,
        int groupSize,
        int elementsPerThread,
        int vectorWidth,
        int waveSize,
        int tileSize,
        int paddedTileCount,
        int definedTileSize,
        int definedTileBins)
    {
        if (backend is not (1 or 2))
            throw new InvalidDataException($"Candidate '{candidate.Id}' Crowd/VFX backend must be 1 or 2.");
        if (groupSize is <= 0 or > 1024 || !BitOperations.IsPow2((uint)groupSize))
            throw new InvalidDataException($"Candidate '{candidate.Id}' group size must be a power of two in 1..1024.");
        if (elementsPerThread is <= 0 or > 16)
            throw new InvalidDataException($"Candidate '{candidate.Id}' elements per thread must be in 1..16.");
        if (vectorWidth is not (1 or 4) || (vectorWidth == 4 && (elementsPerThread & 3) != 0))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width 4 requires an EPT divisible by four.");
        if (waveSize is not (0 or 32 or 64))
            throw new InvalidDataException($"Candidate '{candidate.Id}' wave size must be 0, 32, or 64.");
        if (waveSize != 0 && manifest.ShaderModel is not ("6_6" or "6_7"))
            throw new InvalidDataException($"Candidate '{candidate.Id}' fixed wave size requires shader model 6_6+.");
        if (definedTileSize != tileSize || definedTileBins != paddedTileCount)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' compile-time tile shape does not match the workload ({tileSize}px, {paddedTileCount} bins).");

        if (backend == 2)
        {
            int scale = OptionalDefine(candidate, "HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 1);
            int groups = OptionalDefine(candidate, "HLSLPERF_SINGLE_PASS_GROUPS", 256);
            int replicas = OptionalDefine(candidate, "HLSLPERF_CROWD_TILE_REPLICAS", 1);
            if (scale is <= 0 or > 16 || checked(elementsPerThread * scale) > 64)
                throw new InvalidDataException($"Candidate '{candidate.Id}' fused items per thread must be in 1..64.");
            if (groups is <= 0 or > 65_535)
                throw new InvalidDataException($"Candidate '{candidate.Id}' persistent group limit must be in 1..65,535.");
            if (replicas is <= 0 or > 8 || checked(paddedTileCount * replicas * sizeof(uint)) > 32 * 1024)
                throw new InvalidDataException($"Candidate '{candidate.Id}' tile replicas exceed the 32 KiB LDS budget.");
        }
    }

    private static uint[] GenerateAgents(int count, int seed)
    {
        uint[] values = new uint[count];
        uint state = unchecked((uint)seed);
        for (int index = 0; index < values.Length; ++index)
        {
            state = unchecked(state * 1_664_525u + 1_013_904_223u);
            values[index] = state ^ (state >> 16) ^ unchecked((uint)index * 2_246_822_519u);
        }
        return values;
    }

    private static bool IsVisible(uint seed, int frame, int width, uint visibilityMask) =>
        (Hash32(seed ^ 0xd1b54a35u) & visibilityMask) == 0 && LocalX(seed, frame, width) < (uint)width;

    private static (int X, int Y) Position(uint seed, int frame, int width, int height) =>
        (checked((int)LocalX(seed, frame, width)), checked((int)LocalY(seed, frame, height)));

    private static uint LocalX(uint seed, int frame, int width)
    {
        uint unsignedWidth = checked((uint)width);
        uint worldWidth = checked(unsignedWidth * 4);
        uint origin = Hash32(seed ^ 0x9e3779b9u) % worldWidth;
        uint speed = 1 + ((seed >> 3) & 3);
        uint moving = unchecked(origin + checked((uint)frame) * speed) % worldWidth;
        uint camera = checked((uint)frame * 7u) % worldWidth;
        return (moving + worldWidth - camera) % worldWidth;
    }

    private static uint LocalY(uint seed, int frame, int height)
    {
        uint unsignedHeight = checked((uint)height);
        uint origin = Hash32(seed ^ 0x85ebca6bu) % unsignedHeight;
        uint delta = checked((uint)frame) * (1 + ((seed >> 7) & 1));
        return (seed & 0x20) == 0
            ? (origin + delta) % unsignedHeight
            : (origin + unsignedHeight - (delta % unsignedHeight)) % unsignedHeight;
    }

    private static void InitializeBackground(uint[] atlas, int width, int height, int frame)
    {
        int frameOffset = checked(frame * width * height);
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                uint red = 3 + checked((uint)(y * 5 / height));
                uint green = 7 + checked((uint)(y * 7 / height));
                uint blue = 18 + checked((uint)(y * 14 / height));
                if (x % 48 == 0 || y % 48 == 0)
                    AddSaturated(ref red, ref green, ref blue, 4, 10, 14);
                uint star = Hash32(unchecked((uint)(y * width + x) + 0xa511e9b3u));
                if ((star & 2047) < 3)
                    AddSaturated(ref red, ref green, ref blue, 28, 36, 48);
                atlas[frameOffset + y * width + x] = red | (green << 8) | (blue << 16) | 0xff000000u;
            }
        }
    }

    private static void Splat(uint[] atlas, int frame, int width, int height, int centerX, int centerY, uint seed)
    {
        uint colorHash = Hash32(seed ^ 0xc2b2ae35u);
        int radius = 1 + (int)(colorHash & 1);
        int kind = (int)((colorHash >> 8) % 3);
        int frameOffset = checked(frame * width * height);
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); ++y)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); ++x)
            {
                int distance = Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY));
                uint power = checked((uint)((radius + 1 - distance) * 52));
                int index = frameOffset + y * width + x;
                uint packed = atlas[index];
                uint red = packed & 255;
                uint green = (packed >> 8) & 255;
                uint blue = (packed >> 16) & 255;
                switch (kind)
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
                atlas[index] = red | (green << 8) | (blue << 16) | 0xff000000u;
            }
        }
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

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;

    private static uint CeilDiv(int value, long divisor) =>
        checked((uint)((value + divisor - 1) / divisor));

    private static KernelDispatch DispatchForGroups(uint groupCount)
    {
        if (groupCount == 0)
            throw new InvalidDataException("A Crowd/VFX plan cannot dispatch zero groups.");
        uint x = Math.Min(65_535u, groupCount);
        uint y = (groupCount + x - 1) / x;
        return new KernelDispatch(x, y);
    }

    private sealed record OracleKey(
        int AgentCount,
        int Width,
        int Height,
        int FrameCount,
        int Seed,
        uint VisibilityMask,
        int TileSize);

    private sealed record OracleCache(
        OracleKey Key,
        byte[] Agents,
        byte[] Atlas,
        string AtlasHash,
        int[] VisibleCounts,
        int TileCountX,
        int TileCountY,
        int PaddedTileCount);

    private sealed class ScanScratch
    {
        private readonly IReadOnlyList<Level> levels;
        private readonly long blockSize;

        private ScanScratch(IReadOnlyList<Level> levels, long blockSize)
        {
            this.levels = levels;
            this.blockSize = blockSize;
        }

        public string Output => levels[0].Output;

        public static ScanScratch Declare(
            List<KernelBufferSpec> buffers,
            string prefix,
            int count,
            long blockSize)
        {
            List<Level> levels = [];
            int levelCount = count;
            for (int levelIndex = 0; ; ++levelIndex)
            {
                uint groups = CeilDiv(levelCount, blockSize);
                KernelDispatch dispatch = DispatchForGroups(groups);
                string output = $"{prefix}-scan-{levelIndex}";
                string sums = $"{prefix}-sums-{levelIndex}";
                buffers.Add(new KernelBufferSpec(output, checked(levelCount * sizeof(uint))));
                buffers.Add(new KernelBufferSpec(sums, checked((int)groups * sizeof(uint))));
                levels.Add(new Level(levelCount, groups, dispatch, output, sums));
                if (groups == 1)
                    break;
                levelCount = checked((int)groups);
            }
            return new ScanScratch(levels, blockSize);
        }

        public void AppendPasses(List<KernelPassSpec> passes, string input, string passPrefix)
        {
            string levelInput = input;
            for (int levelIndex = 0; levelIndex < levels.Count; ++levelIndex)
            {
                Level level = levels[levelIndex];
                passes.Add(new KernelPassSpec(
                    $"{passPrefix}-scan-{levelIndex}",
                    "BlockScanPass",
                    level.Dispatch,
                    levelInput,
                    null,
                    level.Output,
                    level.Sums,
                    [(uint)level.Count, 0, 0, 0, 0, 0, level.Dispatch.X, level.Groups]));
                levelInput = level.Sums;
            }

            for (int child = levels.Count - 2; child >= 0; --child)
            {
                Level level = levels[child];
                passes.Add(new KernelPassSpec(
                    $"{passPrefix}-offset-{child}",
                    "AddScanOffsets",
                    level.Dispatch,
                    levels[child + 1].Output,
                    null,
                    level.Output,
                    null,
                    [
                        (uint)level.Count, checked((uint)blockSize), 0, 0, 0, 0,
                        level.Dispatch.X, level.Groups
                    ]));
            }
        }

        private sealed record Level(
            int Count,
            uint Groups,
            KernelDispatch Dispatch,
            string Output,
            string Sums);
    }
}

public sealed record CrowdVfxSceneInfo(
    int AgentCount,
    int Width,
    int Height,
    int FrameCount,
    int TileSize,
    int TileCount,
    int PaddedTileCount,
    uint VisibilityMask,
    IReadOnlyList<int> VisibleCounts,
    string ExpectedAtlasSha256)
{
    internal static CrowdVfxSceneInfo Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, [], string.Empty);
}
