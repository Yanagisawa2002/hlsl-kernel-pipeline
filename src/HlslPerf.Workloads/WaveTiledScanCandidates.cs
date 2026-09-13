using HlslPerf.Core;

namespace HlslPerf.Workloads;

/// <summary>
/// Explicit opt-in descriptors for the locally adapted wave-tiled scan/compaction.
/// These are not registered in any default manifest or benchmark suite.
/// Performance is unmeasured; creating a descriptor does not execute anything.
/// </summary>
public static class WaveTiledScanCandidates
{
    public const string OptInDefine = "HLSLPERF_SCAN_WAVE_TILED";
    public const string ScanEntryPoint = "SinglePassScanWaveTiled";
    public const string CompactionEntryPoint = "FusedCompactWaveTiled";
    public const string ResetEntryPoint = "ResetWaveTiledState";
    public const string PerformanceStatus = "Unmeasured";

    public static KernelCandidate Create(int waveSize = 32) => new(new Dictionary<string, int>
    {
        [OptInDefine] = 1,
        ["HLSLPERF_SCAN_BACKEND"] = 3,
        ["HLSLPERF_SCAN_OPERATOR"] = 1,
        ["HLSLPERF_GROUP_SIZE"] = 256,
        ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
        ["HLSLPERF_SINGLE_PASS_ITEMS_SCALE"] = 4,
        ["HLSLPERF_SINGLE_PASS_GROUPS"] = 256,
        ["HLSLPERF_VECTOR_WIDTH"] = 4,
        ["HLSLPERF_WAVE_SIZE"] = waveSize,
        ["HLSLPERF_WAVE_TILED_MAX_POLLS"] = 4
    });

    internal static bool Validate(KernelCandidate candidate)
    {
        int enabled = Get(OptInDefine, 0);
        if (enabled == 0)
            return false;
        if (enabled != 1)
            throw new InvalidDataException("Wave-tiled scan opt-in must be 0 or 1.");
        int wave = Get("HLSLPERF_WAVE_SIZE", 0);
        int group = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        long items = (long)candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD") *
            Get("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 1);
        if (Get("HLSLPERF_SCAN_BACKEND", 1) != 3 || Get("HLSLPERF_VECTOR_WIDTH", 1) != 4 ||
            Get("HLSLPERF_SCAN_OPERATOR", 1) != 1)
            throw new InvalidDataException("Wave-tiled scan requires backend 3, vector width 4, and uint32 addition.");
        if (wave is not (32 or 64) || group < wave || group > 1024 || (group & (group - 1)) != 0)
            throw new InvalidDataException("Wave-tiled scan requires a power-of-two group of complete fixed 32/64 waves, at most 1024 threads.");
        if (items is < 4 or > 64 || items % 4 != 0)
            throw new InvalidDataException("Wave-tiled scan requires 4..64 items per thread, divisible by four.");
        if (Get("HLSLPERF_WAVE_TILED_MAX_POLLS", 4) is < 1 or > 16)
            throw new InvalidDataException("Wave-tiled scan requires 1..16 bounded status polls.");
        if (Get("HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS", 0) != 0)
            throw new InvalidDataException("Wave-tiled scan does not implement diagnostic counters.");
        return true;

        int Get(string name, int fallback) => candidate.Defines.TryGetValue(name, out int value) ? value : fallback;
    }
}
