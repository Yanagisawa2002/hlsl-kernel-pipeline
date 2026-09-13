using System.Numerics;
using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

// Pure CPU algebra/plan tests. No executor, GPU, native library or clock is used.
public sealed class RadixTileTests
{
    [Theory]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(8, false)] [InlineData(8, true)]
    public void StableTileModelMatchesIndependentComparisonSort(int radix, bool pairs)
    {
        int[] counts = [0, 1, 31, 32, 33, 127, 128, 129, 255, 256, 257, 511, 512, 513, 1025];
        int[] bits = [1, 4, 5, 8, 9, 16, 31, 32];
        foreach (int count in counts)
            foreach (int bitCount in bits)
                foreach (int pattern in Enumerable.Range(0, 5))
                {
                    uint state = 0x917923u;
                    uint[] keys = Enumerable.Range(0, count).Select(i => pattern switch
                    {
                        0 => Next(ref state), 1 => Next(ref state) % 7,
                        2 => uint.MaxValue, 3 => (uint)(count - i),
                        _ => (i % 4) switch { 0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u, _ => 1u }
                    }).ToArray();
                    // Arbitrary payload values: ties cannot be sorted numerically by payload.
                    uint[]? payloads = pairs ? Enumerable.Range(0, count).Select(_ => Next(ref state)).ToArray() : null;
                    var model = Model(keys, payloads, bitCount, radix);
                    Assert.Equal(RadixSortContract.StableOracle(keys, payloads, bitCount), model);
                }
    }

    [Theory]
    [InlineData(1)] [InlineData(511)] [InlineData(512)] [InlineData(513)] [InlineData(1025)]
    public void ChunkAndBinPrefixesEqualFlatExclusiveHistogramScan(int tiles)
    {
        foreach (int radix in new[] { 4, 8 })
        {
            var layout = RadixTileLayout.Create(tiles * 512 - 7, 32, radix, false);
            uint state = 0x6399127;
            uint[] histogram = Enumerable.Range(0, layout.HistogramValues).Select(_ => Next(ref state) & 7).ToArray();
            uint[] prefix = PrefixModel(layout, histogram);
            uint expected = 0;
            for (int bin = 0; bin < layout.Bins; bin++)
                for (int tile = 0; tile < tiles; tile++)
                {
                    int index = bin * tiles + tile;
                    Assert.Equal(expected, Offset(layout, prefix, bin, tile));
                    expected += histogram[index];
                }
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void PlansHaveFourStagesPerDigitAndOnlyRequiredRecordBuffers(bool pairs, bool v2)
    {
        foreach (int count in new[] { 1, 513 })
            foreach (int radix in new[] { 4, 8 })
                foreach (int bits in new[] { 1, 5, 9, 31, 32 })
                {
                    var manifest = Manifest(count, bits, pairs, v2);
                    var plan = BuiltinWorkloads.Resolve(manifest).Build(manifest, Candidate(radix, pairs));
                    var layout = RadixTileLayout.Create(count, bits, radix, pairs);
                    int digits = (bits + radix - 1) / radix;
                    bool split = pairs && v2;
                    plan.Validate();
                    Assert.Equal(digits * 4, plan.Passes.Count);
                    Assert.DoesNotContain(plan.Passes, pass => pass.EntryPoint == "SplitRadixPairs");
                    Assert.Equal(split ? "ScatterRadixTileToPairs" : "ScatterRadixTile", plan.Passes[^1].EntryPoint);
                    Assert.Equal(Math.Min(2, digits - (split ? 1 : 0)),
                        plan.Buffers.Count(b => b.Name is "radix-a" or "radix-b"));
                    Assert.Equal(split ? 2 : 1, plan.GetVerifiedOutputs().Count());
                    Assert.Equal(layout.PrefixBytes, plan.Buffers.Single(b => b.Name == "radix-tile-prefix").ByteLength);
                    for (int digit = 0; digit < digits; digit++)
                    {
                        var group = plan.Passes.Skip(digit * 4).Take(4).ToArray();
                        Assert.Equal(new[] { "BuildRadixHistogram", "PrefixRadixHistogramTiles", "PrefixRadixHistogramBins" },
                            group.Take(3).Select(p => p.EntryPoint));
                        Assert.Equal((uint)layout.PrefixTiles, group[0].Constants[4]);
                        Assert.Equal((uint)layout.Tiles, group[0].Constants[5]);
                        Assert.Equal((uint)layout.SummaryValues, group[1].Constants[7]);
                        Assert.Equal(group[1].Output0, group[2].Output0);
                        Assert.Equal(group[2].Output0, group[3].Input1);
                    }
                    if (v2) Assert.All(plan.Passes.Skip(1), pass => Assert.Single(pass.DependsOn));
                    var legacy = BuiltinWorkloads.Resolve(manifest).Build(manifest,
                        Candidate(radix, pairs) with { Defines = new Dictionary<string, int>(Candidate(radix, pairs).Defines)
                            { ["HLSLPERF_RADIX_TILE"] = 0 } });
                    Assert.Equal(legacy.GetVerifiedOutputs().Select(o => o.ExpectedSha256), plan.GetVerifiedOutputs().Select(o => o.ExpectedSha256));
                    Assert.Equal(digits, RadixSortContract.Describe(plan, bits, radix).DigitPasses);
                }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyTileCandidateRetainsDeterministicSentinels(bool pairs)
    {
        var manifest = Manifest(0, 32, pairs, true);
        var plan = BuiltinWorkloads.Resolve(manifest).Build(manifest, Candidate(8, pairs));
        Assert.Equal("EmptyRadix", plan.Passes[0].EntryPoint);
        Assert.Equal(pairs ? 2 : 1, plan.Passes.Count);
        Assert.Equal(0, RadixSortContract.Describe(plan, 32, 8).DigitPasses);
        Assert.DoesNotContain(plan.Buffers, b => b.Name == "radix-histogram");
    }

    [Theory]
    [InlineData("HLSLPERF_GROUP_SIZE", 256)]
    [InlineData("HLSLPERF_ELEMENTS_PER_THREAD", 2)]
    [InlineData("HLSLPERF_RADIX_BITS", 1)]
    [InlineData("HLSLPERF_SCAN_BACKEND", 1)]
    [InlineData("HLSLPERF_WAVE_SIZE", 0)]
    [InlineData("HLSLPERF_VECTOR_WIDTH", 4)]
    [InlineData("HLSLPERF_RADIX_RANK_BALLOT", 1)]
    [InlineData("HLSLPERF_RADIX_TILE", 2)]
    public void InvalidTileConfigurationFailsBeforeOracleAllocation(string define, int value)
    {
        var manifest = Manifest(536_870_911, 32, false, true);
        var candidate = Candidate(8, false);
        candidate = candidate with { Defines = new Dictionary<string, int>(candidate.Defines) { [define] = value } };
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(manifest, candidate));
    }

    [Fact]
    public void LegacyBallotRestrictionsAreValidatedBeforeAllocation()
    {
        var manifest = Manifest(536_870_911, 32, false, true);
        var candidate = Candidate(8, false) with { Defines = new Dictionary<string, int>(Candidate(8, false).Defines)
            { ["HLSLPERF_RADIX_TILE"] = 0, ["HLSLPERF_RADIX_RANK_BALLOT"] = 1 } };
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(manifest, candidate));
    }

    [Fact]
    public void AddressAndDispatchLimitsCanBeCheckedWithoutAllocatingInputs()
    {
        var layout = RadixTileLayout.Create(536_870_911, 32, 8, false);
        Assert.Equal(1_048_576, layout.Tiles);
        Assert.Equal(2048, layout.PrefixTiles);
        Assert.True(layout.PrefixBytes > layout.HistogramBytes);
        Assert.True(layout.DigitStages(24, 255)[0].Dispatch.Y > 1);
        Assert.True(layout.DigitStages(24, 255)[1].Dispatch.Y > 1);
        Assert.InRange(layout.ScatterSharedBytes(true), 1, 32768);
        Assert.Throws<InvalidDataException>(() => RadixTileLayout.Create(268_435_456, 32, 8, true));
        Assert.Throws<InvalidDataException>(() => layout.DigitStages(31, 255));
        Assert.Throws<InvalidDataException>(() => layout.DigitStages(0, 5));
    }

    [Fact]
    public void NativeContractPreservesTwoToThe28PairsWithoutRelaxingManagedBufferAbi()
    {
        var layout = RadixTileLayout.CreateForNativeBuffers(1 << 28, 32, 8, true);
        var plan = layout.DescribePlan(splitPairs: true);
        Assert.Equal(1L << 31, layout.RecordBytes);
        Assert.Equal(1L << 31, plan.Buffers.Single(b => b.Name == "input").ByteLength);
        Assert.Equal(16, plan.Passes.Count);
        Assert.Equal(new[] { "sorted-keys", "sorted-payloads" }, plan.Outputs);
        Assert.Equal("ScatterRadixTileToPairs", plan.Passes[^1].EntryPoint);
        Assert.Equal(RadixTileLayout.PerformanceStatus, plan.Status);
        Assert.Throws<InvalidDataException>(() => RadixTileLayout.Create(1 << 28, 32, 8, true));
        Assert.Throws<InvalidDataException>(() => RadixTileLayout.CreateForNativeBuffers(1 << 29, 32, 8, true));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(513)]
    public void ExportedScheduleIsTheManagedWorkloadsActualSchedule(int count)
    {
        var layout = RadixTileLayout.Create(count, 32, 8, true);
        var description = layout.DescribePlan(true);
        var manifest = Manifest(count, 32, true, true);
        var plan = BuiltinWorkloads.Resolve(manifest).Build(manifest, new(layout.Defines));
        Assert.Equal(description.Buffers.OrderBy(b => b.Name).Select(b => (b.Name, b.ByteLength)),
            plan.Buffers.OrderBy(b => b.Name).Select(b => (b.Name, (long)b.ByteLength)));
        Assert.Equal(description.Outputs, plan.GetVerifiedOutputs().Select(o => o.Resource));
        Assert.Equal(description.Passes.Count, plan.Passes.Count);
        for (int i = 0; i < description.Passes.Count; i++)
        {
            var expected = description.Passes[i]; var actual = plan.Passes[i];
            Assert.Equal((expected.Name, expected.EntryPoint, expected.Dispatch, expected.Input0, expected.Input1, expected.Output0, expected.Output1),
                (actual.Name, actual.EntryPoint, actual.Dispatch, actual.Input0, actual.Input1, actual.Output0, actual.Output1));
            Assert.Equal(expected.Constants, actual.Constants);
            Assert.Equal(expected.DependsOn, actual.DependsOn);
        }
    }

    private static byte[] Model(uint[] input, uint[]? payloads, int bitCount, int radix)
    {
        if (input.Length == 0) return RadixSortContract.Pack(input, payloads);
        var layout = RadixTileLayout.Create(input.Length, bitCount, radix, payloads is not null);
        var records = input.Select((key, i) => (Key: key, Payload: payloads?[i] ?? 0)).ToArray();
        for (int shift = 0; shift < bitCount; shift += radix)
        {
            uint mask = (1u << Math.Min(radix, bitCount - shift)) - 1;
            uint[] histogram = new uint[layout.HistogramValues];
            for (int i = 0; i < records.Length; i++) histogram[((records[i].Key >> shift) & mask) * layout.Tiles + i / 512]++;
            uint[] prefix = PrefixModel(layout, histogram);
            var destination = new (uint Key, uint Payload)[records.Length];
            bool[] written = new bool[records.Length];
            // Reverse tile and segment execution to expose arrival-order ranking.
            for (int tile = layout.Tiles - 1; tile >= 0; tile--)
            {
                int start = tile * 512, valid = Math.Min(512, records.Length - start);
                uint[] counts = new uint[layout.Bins * 16];
                int[] ranks = new int[valid];
                for (int segment = 15; segment >= 0; segment--)
                    for (int lane = 31; lane >= 0; lane--)
                    {
                        int local = segment * 32 + lane;
                        if (local >= valid) continue;
                        uint digit = (records[start + local].Key >> shift) & mask;
                        uint matches = 0;
                        for (int other = 0; other < 32 && segment * 32 + other < valid; other++)
                            if (((records[start + segment * 32 + other].Key >> shift) & mask) == digit) matches |= 1u << other;
                        ranks[local] = BitOperations.PopCount(matches & ((1u << lane) - 1));
                        if (ranks[local] == 0) counts[digit * 16 + segment] = (uint)BitOperations.PopCount(matches);
                    }
                uint[] binBases = new uint[layout.Bins];
                uint binBase = 0;
                for (int bin = 0; bin < layout.Bins; bin++)
                {
                    binBases[bin] = binBase;
                    uint subtotal = 0;
                    for (int segment = 0; segment < 16; segment++)
                    {
                        int index = bin * 16 + segment;
                        uint count = counts[index]; counts[index] = subtotal; subtotal += count;
                    }
                    Assert.Equal(histogram[bin * layout.Tiles + tile], subtotal);
                    binBase += subtotal;
                }
                Assert.Equal((uint)valid, binBase);
                var localRecords = new (uint Key, uint Payload)[valid];
                bool[] localWritten = new bool[valid];
                for (int local = valid - 1; local >= 0; local--)
                {
                    uint digit = (records[start + local].Key >> shift) & mask;
                    int index = (int)(binBases[digit] + counts[digit * 16 + local / 32]) + ranks[local];
                    Assert.False(localWritten[index]); localWritten[index] = true;
                    localRecords[index] = records[start + local];
                }
                Assert.All(localWritten, value => Assert.True(value));
                for (int local = 0; local < valid; local++)
                {
                    int digit = (int)((localRecords[local].Key >> shift) & mask);
                    uint index = Offset(layout, prefix, digit, tile) + (uint)local - binBases[digit];
                    Assert.False(written[index]); written[index] = true; destination[index] = localRecords[local];
                }
            }
            Assert.All(written, value => Assert.True(value));
            records = destination;
        }
        return RadixSortContract.Pack(records.Select(r => r.Key).ToArray(), payloads is null ? null : records.Select(r => r.Payload).ToArray());
    }

    private static uint[] PrefixModel(RadixTileLayout layout, uint[] histogram)
    {
        uint[] prefix = Enumerable.Repeat(0xcdcdcdcdu, layout.PrefixBytes / 4).ToArray();
        uint[] summaries = Enumerable.Repeat(0xffffffffu, layout.SummaryValues).ToArray();
        for (int bin = 0; bin < layout.Bins; bin++)
            for (int chunk = 0; chunk < layout.PrefixTiles; chunk++)
            {
                uint sum = 0;
                for (int tile = chunk * 512; tile < Math.Min((chunk + 1) * 512, layout.Tiles); tile++)
                {
                    int index = bin * layout.Tiles + tile;
                    prefix[index] = sum; sum += histogram[index];
                }
                summaries[bin * layout.PrefixTiles + chunk] = sum;
            }
        uint binBase = 0;
        for (int bin = 0; bin < layout.Bins; bin++)
        {
            uint sum = 0;
            for (int chunk = 0; chunk < layout.PrefixTiles; chunk++)
            {
                int index = bin * layout.PrefixTiles + chunk;
                prefix[layout.SummaryOffset + index] = sum; sum += summaries[index];
            }
            prefix[layout.BinOffset + bin] = binBase; binBase += sum;
        }
        return prefix;
    }

    private static uint Offset(RadixTileLayout layout, uint[] prefix, int bin, int tile) =>
        prefix[bin * layout.Tiles + tile] + prefix[layout.SummaryOffset + bin * layout.PrefixTiles + tile / 512] + prefix[layout.BinOffset + bin];

    private static uint Next(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state;
    }

    private static KernelCandidate Candidate(int radix, bool pairs) => new(new Dictionary<string, int>
    {
        ["HLSLPERF_GROUP_SIZE"] = 128, ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
        ["HLSLPERF_SCAN_BACKEND"] = 2, ["HLSLPERF_SCAN_OPERATOR"] = 1,
        ["HLSLPERF_RADIX_BITS"] = radix, ["HLSLPERF_RADIX_PAIRS"] = pairs ? 1 : 0,
        ["HLSLPERF_WAVE_SIZE"] = 32, ["HLSLPERF_VECTOR_WIDTH"] = 1, ["HLSLPERF_RADIX_TILE"] = 1
    });

    private static TuningManifest Manifest(int count, int bits, bool pairs, bool v2) => new()
    {
        Name = "tile-radix-cpu", KernelPath = "radix-sort.hlsl", ShaderModel = "6_6",
        KernelAbiVersion = v2 ? KernelAbiV2.Id : KernelAbiV1.Id, SchemaVersion = "3.0", WorkItemCount = Math.Max(1, count),
        Workload = new() { Id = pairs ? "radix-sort-pairs-u32-v1" : "radix-sort-u32-v1",
            Parameters = new Dictionary<string, long> { ["elementCount"] = count, ["bitCount"] = bits, ["keyPattern"] = 2, ["keyDomain"] = 2 } },
        Axes = []
    };
}
