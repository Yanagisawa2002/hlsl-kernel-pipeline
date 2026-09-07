using System.Runtime.InteropServices;
using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class RadixSortTests
{
    [Fact]
    public void ComparisonOraclePreservesOrderRatherThanSortingPayloadValues()
    {
        uint[] result = MemoryMarshal.Cast<byte, uint>(RadixSortContract.StableOracle(
            [0x80000001, 0, 1, uint.MaxValue, 0x80000000, 1], [99, 20, 7, 2, 3, 1], 1)).ToArray();
        Assert.Equal(new uint[] { 0, 20, 0x80000000, 3, 0x80000001, 99, 1, 7, uint.MaxValue, 2, 1, 1 }, result);
    }

    [Fact]
    public void DuplicatePayloadSwapChangesOracleHashEvenWhenSortedKeysMatch()
    {
        byte[] expected = RadixSortContract.StableOracle([7, 7, 7], [0, 1, 2]);
        byte[] unstable = RadixSortContract.Pack([7, 7, 7], [1, 0, 2]);
        Assert.NotEqual(ContentHash.Sha256(expected), ContentHash.Sha256(unstable));
        Assert.Equal(new uint[] { 7, 0, 7, 1, 7, 2 }, MemoryMarshal.Cast<byte, uint>(expected).ToArray());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 5)] [InlineData(129, 31)] [InlineData(4097, 32)]
    public void AllDigitsAgreeOnStableOracleAndSplitOutputs(int count, int bits)
    {
        List<string> hashes = [];
        foreach (int radix in new[] { 1, 4, 8 })
        {
            var manifest = Manifest(count, bits, true, KernelAbiV2.Id);
            var plan = BuiltinWorkloads.Resolve(manifest).Build(manifest, Candidate(radix, true));
            plan.Validate();
            Assert.Equal(count, plan.LogicalItemCount);
            Assert.Equal(2, plan.GetVerifiedOutputs().Count());
            Assert.Equal("sorted-payloads", plan.AdditionalVerifiedOutputs.Single().Resource);
            hashes.Add(plan.ExpectedSha256 + plan.AdditionalVerifiedOutputs.Single().ExpectedSha256);
            if (count > 0)
                Assert.Equal((bits + radix - 1) / radix,
                    plan.Passes.Count(pass => pass.EntryPoint.StartsWith("ScatterRadix", StringComparison.Ordinal)));
        }
        Assert.Single(hashes.Distinct());
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(8)]
    public void PackedV1ValidatesPayloadAndRetainsOriginalBinaryEntryPoints(int radix)
    {
        var manifest = Manifest(257, 5, true, KernelAbiV1.Id);
        var plan = BuiltinWorkloads.Resolve(manifest).Build(manifest, Candidate(radix, true));
        Assert.Equal(KernelAbiV1.Id, plan.AbiVersion);
        Assert.Empty(plan.AdditionalVerifiedOutputs);
        Assert.Equal(257 * 8, plan.Buffers.Single(buffer => buffer.Name == plan.VerifiedResource).ByteLength);
        Assert.Contains(plan.Passes, pass => pass.EntryPoint == (radix == 1 ? "ScatterRadixBit" : "ScatterRadixDigit"));
        if (radix != 1) Assert.Equal(radix == 4 ? 1u : 31u, plan.Passes.Last().Constants[3]);
    }

    [Theory]
    [InlineData(-1, 32, false)] [InlineData(10, 0, false)] [InlineData(10, 33, false)]
    [InlineData(536870912, 32, false)] [InlineData(268435456, 32, true)]
    public void InvalidDimensionsFailBeforeAllocation(int count, int bits, bool pairs) =>
        Assert.Throws<InvalidDataException>(() => RadixSortContract.ValidateDimensions(count, bits, pairs));

    [Fact]
    public void MaximumByteAddressableDimensionsCanBeCheckedWithoutAllocation()
    {
        RadixSortContract.ValidateDimensions(536870911, 32, false);
        RadixSortContract.ValidateDimensions(268435455, 32, true);
    }

    [Fact]
    public void HistogramOverflowAndUnsupportedBlocksFailBeforeOracleAllocation()
    {
        var manifest = Manifest(300_000_000, 32, false, KernelAbiV1.Id);
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(manifest).Build(manifest, Candidate(8, false)));
        Assert.Throws<InvalidDataException>(() => BuiltinWorkloads.Resolve(Manifest(0, 32, false, KernelAbiV1.Id))
            .Build(Manifest(0, 32, false, KernelAbiV1.Id), Candidate(4, false)));
    }

    [Fact]
    public void LargePlanAccountsForScratchAndTwoDimensionalDispatch()
    {
        // Exercise dispatch overflow without allocating a count near the maximum byte ABI.
        var manifest = Manifest(131071, 5, false, KernelAbiV1.Id);
        var candidate = Candidate(4, false) with { Defines = new Dictionary<string, int>
        {
            ["HLSLPERF_GROUP_SIZE"] = 2, ["HLSLPERF_ELEMENTS_PER_THREAD"] = 1,
            ["HLSLPERF_RADIX_BITS"] = 4, ["HLSLPERF_SCAN_BACKEND"] = 1
        }};
        var plan = BuiltinWorkloads.Resolve(manifest).Build(manifest, candidate);
        Assert.Equal(2u, plan.Passes.First().Dispatch.Y);
        var cost = RadixSortContract.Describe(plan, 5, 4);
        Assert.Equal(2, cost.DigitPasses);
        Assert.Equal(plan.Passes.Count, cost.DispatchPasses);
        Assert.Equal(cost.AllocatedBytes - 2L * 131071 * 4, cost.ScratchBytes);
    }

    private static KernelCandidate Candidate(int radix, bool pairs) => new(new Dictionary<string, int>
    {
        ["HLSLPERF_GROUP_SIZE"] = 64, ["HLSLPERF_ELEMENTS_PER_THREAD"] = 2,
        ["HLSLPERF_SCAN_BACKEND"] = 1, ["HLSLPERF_RADIX_BITS"] = radix,
        ["HLSLPERF_RADIX_PAIRS"] = pairs ? 1 : 0
    });

    private static TuningManifest Manifest(int count, int bits, bool pairs, string abi) => new()
    {
        Name = "radix-test", KernelPath = "radix-sort.hlsl", KernelAbiVersion = abi,
        SchemaVersion = "3.0", WorkItemCount = Math.Max(1, count),
        Workload = new() { Id = pairs ? "radix-sort-pairs-u32-v1" : "radix-sort-u32-v1",
            Parameters = new Dictionary<string, long> { ["elementCount"] = count, ["bitCount"] = bits,
                ["keyPattern"] = 2, ["keyDomain"] = 2 } },
        Axes = [new() { Name = "HLSLPERF_RADIX_BITS", Values = [1, 4, 8] }]
    };
}
