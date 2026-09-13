using HlslPerf.Core;

namespace HlslPerf.Workloads;

/// <summary>
/// Layout for the opt-in tiled radix candidate (Unmeasured), computed without
/// allocating records or scratch. Offsets are uint elements, not bytes.
/// No device or timing API is used.
/// </summary>
public sealed record RadixTileLayout
{
    public const int GroupSize = 128;
    public const int ItemsPerThread = 4;
    public const int TileSize = GroupSize * ItemsPerThread;
    public const int PrefixTileSize = 512;
    public const string PerformanceStatus = "Unmeasured";

    public int Count { get; }
    public int BitCount { get; }
    public int RadixBits { get; }
    public bool Pairs { get; }
    public long RecordBytes => Math.Max(1L, Count) * (Pairs ? 8 : 4);
    public int Bins { get; }
    public int Tiles { get; }
    public int PrefixTiles { get; }
    public int HistogramValues { get; }
    public int SummaryValues { get; }
    public int SummaryOffset => HistogramValues;
    public int BinOffset => HistogramValues + SummaryValues;
    public int HistogramBytes => checked(HistogramValues * sizeof(uint));
    public int SummaryBytes => checked(SummaryValues * sizeof(uint));
    public int PrefixBytes => checked((BinOffset + Bins) * sizeof(uint));

    private RadixTileLayout(int count, int bitCount, int radixBits, bool pairs)
    {
        Count = count;
        BitCount = bitCount;
        RadixBits = radixBits;
        Pairs = pairs;
        Bins = 1 << radixBits;
        Tiles = (int)(((long)count + TileSize - 1) / TileSize);
        PrefixTiles = (Tiles + PrefixTileSize - 1) / PrefixTileSize;
        long histogram = (long)Bins * Tiles, summaries = (long)Bins * PrefixTiles;
        if ((histogram + summaries + Bins) * sizeof(uint) > int.MaxValue)
            throw new InvalidDataException("Tiled radix prefix scratch exceeds the signed 32-bit buffer ABI.");
        HistogramValues = (int)histogram;
        SummaryValues = (int)summaries;
    }

    public static RadixTileLayout Create(int count, int bitCount, int radixBits, bool pairs)
    {
        RadixSortContract.ValidateDimensions(count, bitCount, pairs);
        if (radixBits is not (4 or 8))
            throw new InvalidDataException("Tiled radix requires four- or eight-bit digits.");
        return new(count, bitCount, radixBits, pairs);
    }

    /// <summary>
    /// Geometry for native hosts with 64-bit allocation lengths. This does not
    /// relax KernelBufferSpec's signed-byte ABI. In particular 2^28 pairs need
    /// a 2 GiB packed buffer and cannot be constructed through the managed ABI.
    /// No records are allocated and no device support or residency is implied.
    /// </summary>
    public static RadixTileLayout CreateForNativeBuffers(int count, int bitCount, int radixBits, bool pairs)
    {
        if (count < 0 || bitCount is < 1 or > 32 || radixBits is not (4 or 8) ||
            (long)count * (pairs ? 8 : 4) > uint.MaxValue)
            throw new InvalidDataException("Native radix requires 1..32 low bits, four/eight-bit digits and uint byte-addressable records.");
        return new(count, bitCount, radixBits, pairs);
    }

    public IReadOnlyDictionary<string, int> Defines => new Dictionary<string, int>
    {
        ["HLSLPERF_GROUP_SIZE"] = GroupSize, ["HLSLPERF_ELEMENTS_PER_THREAD"] = ItemsPerThread,
        ["HLSLPERF_SCAN_BACKEND"] = 2, ["HLSLPERF_SCAN_OPERATOR"] = 1,
        ["HLSLPERF_VECTOR_WIDTH"] = 1, ["HLSLPERF_WAVE_SIZE"] = 32,
        ["HLSLPERF_RADIX_BITS"] = RadixBits, ["HLSLPERF_RADIX_PAIRS"] = Pairs ? 1 : 0,
        ["HLSLPERF_RADIX_TILE"] = 1, ["HLSLPERF_RADIX_RANK_BALLOT"] = 0
    };

    /// <summary>Complete AoS-input schedule, without inputs, hashes, device calls or timing.</summary>
    public RadixTilePlan DescribePlan(bool splitPairs)
    {
        if (splitPairs && !Pairs) throw new InvalidDataException("Split output requires pair records.");
        List<RadixTileBuffer> buffers = [];
        List<KernelPassSpec> passes = [];
        int digits = Count == 0 ? 0 : (BitCount + RadixBits - 1) / RadixBits;
        string source;
        if (Count == 0)
        {
            source = "radix-empty";
            buffers.Add(new(source, RecordBytes));
            passes.Add(new("radix-empty", "EmptyRadix", new(1), null, null, source, null, []));
            if (splitPairs)
                passes.Add(new("split-radix-pairs", "SplitRadixPairs", new(1), source, null, "sorted-keys", "sorted-payloads",
                    [0, TileSize, 0, 0, 0, 0, 1, 1]));
        }
        else
        {
            buffers.AddRange([new("input", RecordBytes), new("radix-histogram", HistogramBytes),
                new("radix-tile-prefix", PrefixBytes), new("radix-tile-sums", SummaryBytes)]);
            int recordBuffers = Math.Min(2, digits - (splitPairs ? 1 : 0));
            if (recordBuffers > 0) buffers.Add(new("radix-a", RecordBytes));
            if (recordBuffers > 1) buffers.Add(new("radix-b", RecordBytes));
            source = "input";
            for (int shift = 0, digit = 0; shift < BitCount; shift += RadixBits, digit++)
            {
                uint mask = (1u << Math.Min(RadixBits, BitCount - shift)) - 1;
                IReadOnlyList<RadixTileStage> stages = DigitStages(shift, mask);
                bool finalPairs = splitPairs && digit == digits - 1;
                string destination = finalPairs ? "sorted-keys" : digit % 2 == 0 ? "radix-a" : "radix-b";
                Add(stages[0], source, null, "radix-histogram", null);
                Add(stages[1], "radix-histogram", null, "radix-tile-prefix", "radix-tile-sums");
                Add(stages[2], "radix-tile-sums", null, "radix-tile-prefix", null);
                Add(finalPairs ? stages[3] with { EntryPoint = "ScatterRadixTileToPairs" } : stages[3],
                    source, "radix-tile-prefix", destination, finalPairs ? "sorted-payloads" : null);
                source = destination;

                void Add(RadixTileStage stage, string? input0, string? input1, string output0, string? output1) =>
                    passes.Add(new($"radix-{shift}-tile-{stage.Name}", stage.EntryPoint, stage.Dispatch,
                        input0, input1, output0, output1, stage.Constants));
            }
        }
        if (splitPairs)
        {
            buffers.Add(new("sorted-keys", Math.Max(1L, Count) * sizeof(uint)));
            buffers.Add(new("sorted-payloads", Math.Max(1L, Count) * sizeof(uint)));
        }
        for (int i = 1; i < passes.Count; i++) passes[i] = passes[i] with { DependsOn = [passes[i - 1].Name] };
        return new(PerformanceStatus, Defines, buffers, passes,
            splitPairs ? ["sorted-keys", "sorted-payloads"] : [source], digits);
    }

    /// <summary>Declared LDS for scatter; DXC may eliminate unused entry-point resources.</summary>
    public int ScatterSharedBytes(bool pairs) =>
        checked((Bins * (TileSize / 32) + 2 * Bins + TileSize * (pairs ? 2 : 1) + GroupSize / 32 + 1) * sizeof(uint));

    /// <summary>
    /// Histogram, chunk prefixes, summary/bin prefixes, stable scatter. The final
    /// pair scatter may write SoA directly. Positive counts only; empty uses the ABI guard.
    /// </summary>
    public IReadOnlyList<RadixTileStage> DigitStages(int shift, uint mask)
    {
        if (Count == 0) throw new InvalidDataException("Empty radix has no digit stages.");
        if (shift is < 0 or > 31 || mask == 0 || mask >= Bins || (mask & (mask + 1)) != 0 ||
            ((ulong)mask << shift) > uint.MaxValue)
            throw new InvalidDataException("Radix digit must select consecutive remaining low bits.");
        return [
            Stage("histogram", "BuildRadixHistogram", (uint)Tiles),
            Stage("prefix-tiles", "PrefixRadixHistogramTiles", checked((uint)SummaryValues)),
            Stage("prefix-bins", "PrefixRadixHistogramBins", 1),
            Stage("scatter", "ScatterRadixTile", (uint)Tiles)
        ];

        RadixTileStage Stage(string name, string entry, uint groups)
        {
            KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(groups);
            return new(name, entry, dispatch,
                [(uint)Count, TileSize, (uint)shift, mask, (uint)PrefixTiles, (uint)Tiles, dispatch.X, groups]);
        }
    }
}

public sealed record RadixTileStage(string Name, string EntryPoint, KernelDispatch Dispatch, uint[] Constants);
public sealed record RadixTileBuffer(string Name, long ByteLength);
public sealed record RadixTilePlan(string Status, IReadOnlyDictionary<string, int> Defines,
    IReadOnlyList<RadixTileBuffer> Buffers, IReadOnlyList<KernelPassSpec> Passes,
    IReadOnlyList<string> Outputs, int DigitPasses);
