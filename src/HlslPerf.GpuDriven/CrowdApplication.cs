using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using HlslPerf.Core;

namespace HlslPerf.GpuDriven;

public sealed record CrowdApplicationScene(int AgentCount, int Seed, uint VisibilityMask,
    int Width = 480, int Height = 270, int Frames = 12);

/// <summary>The existing controlled renderer without CPU-oracle scheduling.
/// Input generation, plan construction and execution are separate lifecycle steps.</summary>
public static class CrowdApplication
{
    public static readonly string[] Arms = ["hierarchical", "fused", "wave-tiled", "rts"];

    public static byte[] GenerateAgents(int count, int seed)
    {
        if (count <= 0 || count > 16_777_216) throw new ArgumentOutOfRangeException(nameof(count));
        uint[] values = new uint[count];
        uint state = unchecked((uint)seed);
        for (int i = 0; i < count; i++)
        {
            state = unchecked(state * 1_664_525u + 1_013_904_223u);
            values[i] = state ^ (state >> 16) ^ unchecked((uint)i * 2_246_822_519u);
        }
        return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }

    // The hash is verification metadata only. It cannot influence any allocation,
    // dispatch, input or shader constant. The runtime never executes the CPU oracle.
    public static UnifiedOperationPlan Build(string repository, CrowdApplicationScene scene,
        byte[] agents, string arm, string expectedAtlasSha256)
    {
        int n = scene.AgentCount, aligned = checked((n + 3) / 4 * 4);
        if (!Arms.Contains(arm) || n <= 0 || n > 16_777_216 || agents.Length != checked(n * 4) ||
            scene.Width <= 0 || scene.Height <= 0 || scene.Frames is < 1 or > 24 ||
            (scene.VisibilityMask & (scene.VisibilityMask + 1)) != 0)
            throw new ArgumentException("Unsupported bounded Crowd/VFX scene or arm.");
        int tilesX = (scene.Width + 15) / 16, tilesY = (scene.Height + 15) / 16;
        int bins = checked((int)BitOperations.RoundUpToPowerOf2((uint)(tilesX * tilesY)));
        if (bins > 1024) throw new ArgumentException("Tile prefix must fit one group.");
        string root = Path.GetFullPath(repository);
        bool fused = arm == "fused";
        Dictionary<string, string> crowdDefines = new()
        {
            ["HLSLPERF_GROUP_SIZE"] = "256", ["HLSLPERF_ELEMENTS_PER_THREAD"] = "4",
            ["HLSLPERF_VECTOR_WIDTH"] = "1", ["HLSLPERF_SCAN_BACKEND"] = "1",
            ["HLSLPERF_CROWD_BACKEND"] = fused ? "2" : "1",
            ["HLSLPERF_CROWD_TILE_BINS"] = bins.ToString(CultureInfo.InvariantCulture),
            ["HLSLPERF_CROWD_TILE_SIZE"] = "16", ["HLSLPERF_CROWD_TILE_REPLICAS"] = "2",
            ["HLSLPERF_WAVE_SIZE"] = "32", ["HLSLPERF_SINGLE_PASS_ITEMS_SCALE"] = "4",
            ["HLSLPERF_SINGLE_PASS_GROUPS"] = "256"
        };
        List<KernelBufferSpec> buffers =
        [new("agents", agents.Length, agents), new("visible-seeds", checked((n + 2) * 4)),
         new("tile-counts", bins * 4), new("tile-offsets", (bins + 1) * 4),
         new("tile-cursors", bins * 4), new("tile-seeds", checked((n + 1) * 4)),
         new("frame-atlas", checked(scene.Width * scene.Height * scene.Frames * 4))];
        List<UnifiedShader> shaders = [];
        List<UnifiedPass> passes = [];
        string CrowdShader(string entry)
        {
            string id = $"crowd/{arm}/{bins}/{entry}";
            if (shaders.All(s => s.Id != id)) shaders.Add(new(id,
                Path.Combine(root, "gpu-driven-demo/crowd-runtime.hlsl"), entry, crowdDefines,
                [Path.Combine(root, "gpu-driven-demo"), Path.Combine(root, "kernels")], []));
            return id;
        }
        void Crowd(string name, string entry, KernelDispatch dispatch, string?[] srvs, string?[] uavs, uint[] constants) =>
            passes.Add(new(name, UnifiedStage.Algorithm, CrowdShader(entry), dispatch, srvs, uavs, constants));
        int scanBlocks = (n + 4095) / 4096;
        List<(int Count, uint Groups, string Output, string Sums)> levels = [];
        if (!fused) buffers.Add(new("visibility-flags", aligned * 4));
        if (arm == "hierarchical")
        {
            int count = n;
            for (int i = 0; ; i++)
            {
                uint groups = (uint)((count + 1023) / 1024);
                string output = "scan-" + i, sums = "sums-" + i;
                buffers.Add(new(output, count * 4)); buffers.Add(new(sums, checked((int)groups * 4)));
                levels.Add((count, groups, output, sums));
                if (groups == 1) break;
                count = checked((int)groups);
            }
        }
        else if (fused) buffers.Add(new("state", checked(8 + 12 * scanBlocks), new byte[8 + 12 * scanBlocks]));
        else
        {
            buffers.Add(new("scan", aligned * 4));
            buffers.Add(new("state", arm == "rts" ? (n + 3071) / 3072 * 4 : 8 + 12 * scanBlocks));
        }
        if (arm == "wave-tiled")
        {
            var defines = new Dictionary<string, string>(crowdDefines)
            {
                ["HLSLPERF_SCAN_WAVE_TILED"] = "1", ["HLSLPERF_SCAN_BACKEND"] = "3",
                ["HLSLPERF_VECTOR_WIDTH"] = "4", ["HLSLPERF_WAVE_TILED_MAX_POLLS"] = "4",
                ["HLSLPERF_WAVE_TILED_INCLUSIVE"] = "0"
            };
            foreach (string entry in new[] { "ResetWaveTiledState", "SinglePassScanWaveTiled" })
                shaders.Add(new("crowd-wave/" + entry, Path.Combine(root, "kernels/scan.hlsl"), entry,
                    defines, [Path.Combine(root, "kernels")], []));
        }
        if (arm == "rts")
        {
            string folder = Path.Combine(root, "third_party/gpu-prefix-sums/GPUPrefixSumsD3D12/Shaders");
            foreach (string entry in new[] { "Reduce", "Scan", "PropagateExclusive" })
                shaders.Add(new("crowd-rts/" + entry, Path.Combine(folder, "ReduceThenScan.hlsl"), entry,
                    new Dictionary<string, string>(), [folder], [])
                    { HlslVersion = 2021, ShaderModel = "6_7", EnableStrictness = false });
        }
        uint classifyGroups = (uint)((n + 1023) / 1024);
        // Bounds are derived only from N. Every group checks the GPU header.
        uint tileGroups = Math.Min(classifyGroups, 32u);
        for (int f = 0; f < scene.Frames; f++)
        {
            string tag = $"f{f}/";
            uint[] classify = [(uint)n, 1024, 0, (uint)scene.Width, (uint)f, scene.VisibilityMask, classifyGroups, classifyGroups];
            if (fused)
            {
                Crowd(tag + "reset", "ResetSinglePassState", new(1), [], ["state"], []);
                Crowd(tag + "compact", "FusedCrowdVisibilityCompact", new((uint)Math.Min(scanBlocks, 256)),
                    ["agents"], ["visible-seeds", "state"], [(uint)n, 4096, (uint)scanBlocks, (uint)scene.Width, (uint)f, scene.VisibilityMask]);
            }
            else
            {
                Crowd(tag + "classify", "ProducePaddedCrowdVisibilityFlags", new(classifyGroups),
                    ["agents"], ["visibility-flags"], classify);
                if (arm == "hierarchical")
                {
                    string input = "visibility-flags";
                    foreach (var level in levels)
                    {
                        Crowd(tag + level.Output, "BlockScanPass", new(level.Groups), [input], [level.Output, level.Sums],
                            [(uint)level.Count, 0, 0, 0, 0, 0, level.Groups, level.Groups]);
                        input = level.Sums;
                    }
                    for (int i = levels.Count - 2; i >= 0; i--)
                    {
                        var level = levels[i];
                        Crowd(tag + "offset-" + i, "AddScanOffsets", new(level.Groups), [levels[i + 1].Output], [level.Output],
                            [(uint)level.Count, 1024, 0, 0, 0, 0, level.Groups, level.Groups]);
                    }
                }
                else if (arm == "wave-tiled")
                {
                    passes.Add(new(tag + "reset", UnifiedStage.Algorithm, "crowd-wave/ResetWaveTiledState", new(1), [], ["state"], [0, 0, (uint)scanBlocks]));
                    passes.Add(new(tag + "scan", UnifiedStage.Algorithm, "crowd-wave/SinglePassScanWaveTiled", new((uint)Math.Min(scanBlocks, 256)),
                        ["visibility-flags"], ["scan", "state"], [(uint)n, 4096, (uint)scanBlocks]));
                }
                else
                {
                    uint blocks = (uint)((n + 3071) / 3072);
                    uint[] constants = [(uint)(aligned / 4), blocks, 1, 0];
                    passes.Add(new(tag + "reduce", UnifiedStage.Algorithm, "crowd-rts/Reduce", new(blocks), [], ["visibility-flags", null, "state"], constants));
                    passes.Add(new(tag + "scan", UnifiedStage.Algorithm, "crowd-rts/Scan", new(1), [], [null, null, "state"], [0, blocks, 0, 0]));
                    passes.Add(new(tag + "propagate", UnifiedStage.Algorithm, "crowd-rts/PropagateExclusive", new(blocks), [], ["visibility-flags", "scan", "state"], constants));
                }
                Crowd(tag + "scatter", "ScatterVisibleCrowdSeeds", new(classifyGroups), ["agents", arm == "hierarchical" ? "scan-0" : "scan"], ["visible-seeds"], classify);
            }
            Crowd(tag + "reset-bins", "ResetCrowdTileBins", new((uint)((bins + 255) / 256)), [], ["tile-counts", "tile-cursors"], [(uint)bins]);
            uint[] tile = [(uint)n, 1024, 0, (uint)scene.Width, (uint)scene.Height, (uint)f, tileGroups, tileGroups];
            Crowd(tag + "histogram", "BuildCrowdTileHistogram", new(tileGroups), ["visible-seeds"], ["tile-counts"], tile);
            Crowd(tag + "tile-prefix", "PrefixCrowdTileHistogram", new(1), ["tile-counts"], ["tile-offsets"], [(uint)bins]);
            Crowd(tag + "tile-scatter", "ScatterCrowdTileSeeds", new(tileGroups), ["visible-seeds", "tile-offsets"], ["tile-seeds", "tile-cursors"], tile);
            Crowd(tag + "raster", "RasterizeCrowdVfx", new((uint)((scene.Width + 7) / 8), (uint)((scene.Height + 7) / 8)),
                ["tile-seeds", "tile-offsets"], ["frame-atlas"], [(uint)n, 0, 0, (uint)scene.Width, (uint)scene.Height, (uint)f, (uint)tilesX, (uint)tilesY]);
        }
        UnifiedOperationPlan plan = new("crowd-" + arm, n, "crowd-vfx-stable-exclusive-rgba-v1", ContentHash.Sha256(agents),
            buffers, shaders, passes, [new("frame-atlas", expectedAtlasSha256)]) { ImmutableInputs = ["agents"] };
        plan.Validate();
        return plan;
    }

    public static Dictionary<string, uint[]> FrameConstants(UnifiedOperationPlan plan, uint firstFrame)
    {
        if (firstFrame > 100_000) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        Dictionary<string, uint[]> overrides = [];
        foreach (var pass in plan.Passes)
        {
            string kind = pass.Name[(pass.Name.IndexOf('/') + 1)..];
            int slot = kind is "classify" or "scatter" or "compact" ? 4 :
                kind is "histogram" or "tile-scatter" or "raster" ? 5 : -1;
            if (slot < 0) continue;
            uint[] constants = pass.Constants.ToArray();
            constants[slot] = checked(constants[slot] + firstFrame);
            if (kind == "raster") constants[2] = firstFrame;
            overrides.Add(pass.Name, constants);
        }
        return overrides;
    }
}
