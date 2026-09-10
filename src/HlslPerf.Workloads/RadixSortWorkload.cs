using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class RadixSortWorkload(bool pairs = false) : IKernelWorkload
{
    public string Id => pairs ? "radix-sort-pairs-u32-v1" : "radix-sort-u32-v1";

    private byte[]? inputData;
    private string? expectedHash;
    private string? expectedKeysHash;
    private string? expectedPayloadsHash;
    private int cachedCount;
    private int cachedSeed;
    private int cachedBitCount;
    private int cachedPattern;
    private int cachedDomain;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 9_181_321);
        int bitCount = spec.GetInt32("bitCount", 32);
        int pattern = spec.GetInt32("keyPattern", 1);
        int domain = spec.GetInt32("keyDomain", 1);
        if (domain is not (1 or 2))
            throw new InvalidDataException("keyDomain must be 1=masked keys or 2=full keys sorted by low bitCount bits.");
        int radixBits = OptionalDefine(candidate, "HLSLPERF_RADIX_BITS", 1);
        int backend = candidate.GetRequired("HLSLPERF_SCAN_BACKEND");
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int waveSize = OptionalDefine(candidate, "HLSLPERF_WAVE_SIZE", 0);
        int tiled = OptionalDefine(candidate, "HLSLPERF_RADIX_TILE", 0);
        int ballot = OptionalDefine(candidate, "HLSLPERF_RADIX_RANK_BALLOT", 0);

        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        RadixSortContract.ValidateDimensions(elementCount, bitCount, pairs);
        if (elementsPerThread is <= 0 or > 16 || (long)groupSize * elementsPerThread < 2)
            throw new InvalidDataException("Radix elements per thread must be in 1..16 and block size at least two.");
        if (radixBits is not (1 or 4 or 8))
            throw new InvalidDataException("HLSLPERF_RADIX_BITS must be 1, 4, or 8.");
        if (OptionalDefine(candidate, "HLSLPERF_RADIX_PAIRS", 0) != (pairs ? 1 : 0))
            throw new InvalidDataException("HLSLPERF_RADIX_PAIRS must match the workload's record format.");
        if (OptionalDefine(candidate, "HLSLPERF_SCAN_OPERATOR", 1) != 1)
            throw new InvalidDataException("Radix requires the addition scan operator.");
        if (backend is not (1 or 2))
            throw new InvalidDataException($"Candidate '{candidate.Id}' radix scan backend must be 1 or 2.");
        if (vectorWidth is not (1 or 4))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width must be 1 or 4.");
        if (vectorWidth == 4 && (elementsPerThread != 4 || pairs))
            throw new InvalidDataException("Radix vector loads require four keys per thread and key-only records.");
        if (waveSize is not (0 or 32 or 64))
            throw new InvalidDataException($"Candidate '{candidate.Id}' wave size must be 0, 32, or 64.");
        if (waveSize != 0 && manifest.ShaderModel is not ("6_6" or "6_7"))
            throw new InvalidDataException($"Candidate '{candidate.Id}' fixed wave size requires shader model 6_6+.");
        if (ballot is not (0 or 1) || (ballot == 1 && (groupSize != 128 || elementsPerThread != 2 || radixBits != 8 || waveSize != 32)))
            throw new InvalidDataException("Ballot radix rank requires group128, two records, eight bits and wave32.");
        if (tiled is not (0 or 1) || (tiled == 1 && (groupSize != RadixTileLayout.GroupSize ||
            elementsPerThread != RadixTileLayout.ItemsPerThread || radixBits is not (4 or 8) ||
            waveSize != 32 || backend != 2 || vectorWidth != 1 || ballot != 0)))
            throw new InvalidDataException("Tiled radix requires group128, four records, four/eight bits, wave32, backend2, scalar loads and no legacy ballot rank.");

        if (elementCount == 0 && manifest.KernelAbiVersion != KernelAbiV2.Id)
            throw new InvalidDataException("Zero-count radix requires ABI v2; v1 accepts positive counts only.");
        long preflightBlock = (long)groupSize * elementsPerThread;
        if (radixBits != 1 && (preflightBlock > 1024 ||
            ((elementCount + preflightBlock - 1) / preflightBlock) * (1 << radixBits) * 4 > int.MaxValue))
            throw new InvalidDataException("Wide radix block or histogram exceeds its supported ABI limit.");
        RadixTileLayout? tileLayout = tiled == 1 ? RadixTileLayout.Create(elementCount, bitCount, radixBits, pairs) : null;
        EnsureOracle(elementCount, seed, bitCount, pattern, domain);
        if (elementCount == 0)
        {
            KernelExecutionPlan empty = new(Id, KernelAbiV2.Id, 0,
                [new("radix-empty", inputData!.Length)],
                [new("radix-empty", "EmptyRadix", new(1), null, null, "radix-empty", null, [])],
                "radix-empty", expectedHash!);
            return Finish(empty, manifest, groupSize, elementsPerThread);
        }
        if (tileLayout is not null)
            return Finish(BuildTiled(tileLayout, manifest.KernelAbiVersion == KernelAbiV2.Id),
                manifest, groupSize, elementsPerThread, splitPairs: false);
        if (radixBits != 1)
            return Finish(BuildWide(elementCount, bitCount, radixBits, groupSize, elementsPerThread), manifest, groupSize, elementsPerThread);
        long blockSize = checked((long)groupSize * elementsPerThread);
        uint logicalGroups = WorkloadData.CeilDiv(elementCount, blockSize);
        KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(logicalGroups);
        List<KernelBufferSpec> buffers =
        [
            new("input", inputData!.Length, inputData),
            new("radix-a", inputData.Length),
            new("radix-b", inputData.Length),
            new("radix-flags", checked(elementCount * sizeof(uint)))
        ];
        ScanHierarchyLayout scan = ScanHierarchyLayout.Declare(
            buffers,
            "radix",
            elementCount,
            blockSize);
        List<KernelPassSpec> passes = [];
        string source = "input";
        for (int bit = 0; bit < bitCount; ++bit)
        {
            string destination = (bit & 1) == 0 ? "radix-a" : "radix-b";
            passes.Add(new KernelPassSpec(
                $"radix-{bit}-flags",
                "ProduceZeroFlags",
                dispatch,
                source,
                null,
                "radix-flags",
                null,
                [
                    (uint)elementCount, checked((uint)blockSize), (uint)bit, 0, 0, 0,
                    dispatch.X, logicalGroups
                ]));
            scan.AppendPasses(passes, "radix-flags", $"radix-{bit}");
            passes.Add(new KernelPassSpec(
                $"radix-{bit}-scatter",
                "ScatterRadixBit",
                dispatch,
                source,
                scan.Output,
                destination,
                null,
                [
                    (uint)elementCount, checked((uint)blockSize), (uint)bit, 0, 0, 0,
                    dispatch.X, logicalGroups
                ]));
            source = destination;
        }

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            buffers,
            passes,
            source,
            expectedHash!);
        return Finish(plan, manifest, groupSize, elementsPerThread);
    }

    private KernelExecutionPlan BuildTiled(RadixTileLayout layout, bool v2)
    {
        bool directPairs = pairs && v2;
        RadixTilePlan description = layout.DescribePlan(directPairs);
        KernelBufferSpec[] buffers = description.Buffers.Select(buffer => new KernelBufferSpec(
            buffer.Name, checked((int)buffer.ByteLength), buffer.Name == "input" ? inputData : null)).ToArray();
        KernelPassSpec[] passes = description.Passes.Select(pass => v2 ? pass : pass with { DependsOn = [] }).ToArray();
        return new(Id, v2 ? KernelAbiV2.Id : KernelAbiV1.Id, layout.Count, buffers, passes, description.Outputs[0],
            directPairs ? expectedKeysHash! : expectedHash!)
        {
            AdditionalVerifiedOutputs = directPairs ? [new("sorted-payloads", expectedPayloadsHash!)] : []
        };
    }

    private KernelExecutionPlan BuildWide(int count, int bitCount, int radixBits, int groupSize, int items)
    {
        int blockSize = checked(groupSize * items);
        if (blockSize > 1024)
            throw new InvalidDataException("Wide radix local ranking supports at most 1024 records per block.");
        uint groups = WorkloadData.CeilDiv(count, blockSize);
        int histogramCount = checked((int)groups * (1 << radixBits));
        int histogramBytes = checked(histogramCount * sizeof(uint));
        KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(groups);
        List<KernelBufferSpec> buffers =
        [
            new("input", inputData!.Length, inputData),
            new("radix-a", inputData.Length), new("radix-b", inputData.Length),
            new("radix-histogram", histogramBytes)
        ];
        ScanHierarchyLayout scan = ScanHierarchyLayout.Declare(buffers, "radix", histogramCount, blockSize);
        List<KernelPassSpec> passes = [];
        string source = "input";
        for (int shift = 0, digit = 0; shift < bitCount; shift += radixBits, digit++)
        {
            string destination = (digit & 1) == 0 ? "radix-a" : "radix-b";
            uint mask = (1u << Math.Min(radixBits, bitCount - shift)) - 1;
            uint[] constants = [(uint)count, (uint)blockSize, (uint)shift, mask, 0, 0, dispatch.X, groups];
            passes.Add(new($"radix-{shift}-histogram", "BuildRadixHistogram", dispatch,
                source, null, "radix-histogram", null, constants));
            scan.AppendPasses(passes, "radix-histogram", $"radix-{shift}");
            passes.Add(new($"radix-{shift}-scatter", "ScatterRadixDigit", dispatch,
                source, scan.Output, destination, null, constants));
            source = destination;
        }
        KernelExecutionPlan plan = new(Id, KernelAbiV1.Id, count, buffers, passes, source, expectedHash!);
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed, int bitCount, int pattern, int domain)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed &&
            cachedBitCount == bitCount && cachedPattern == pattern && cachedDomain == domain)
            return;

        if (pattern is < 1 or > 5)
            throw new InvalidDataException("keyPattern must be 1=random, 2=duplicates, 3=equal, 4=descending, or 5=extremes.");
        uint[] input = WorkloadData.AsUInt32(WorkloadData.GenerateUInt32(elementCount, seed));
        uint mask = bitCount == 32 || domain == 2 ? uint.MaxValue : (1u << bitCount) - 1;
        for (int index = 0; index < input.Length; ++index)
        {
            input[index] = (pattern switch
            {
                2 => input[index] % 7,
                3 => 0x80000001u,
                4 => (uint)(input.Length - index),
                5 => (index % 4) switch { 0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u, _ => 1u },
                _ => input[index]
            }) & mask;
        }
        uint[]? payloads = pairs ? Enumerable.Range(0, elementCount).Select(index => (uint)index).ToArray() : null;
        inputData = RadixSortContract.Pack(input, payloads);
        byte[] expected = RadixSortContract.StableOracle(input, payloads, bitCount);
        expectedHash = ContentHash.Sha256(expected);
        if (pairs)
        {
            uint[] sorted = WorkloadData.AsUInt32(expected);
            uint[] keys = new uint[Math.Max(1, elementCount)];
            uint[] values = new uint[keys.Length];
            for (int index = 0; index < keys.Length; index++)
            {
                keys[index] = sorted[index * 2];
                values[index] = sorted[index * 2 + 1];
            }
            expectedKeysHash = ContentHash.Sha256(WorkloadData.ToBytes(keys));
            expectedPayloadsHash = ContentHash.Sha256(WorkloadData.ToBytes(values));
        }
        cachedCount = elementCount;
        cachedSeed = seed;
        cachedBitCount = bitCount;
        cachedPattern = pattern;
        cachedDomain = domain;
    }

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;

    private KernelExecutionPlan Finish(KernelExecutionPlan plan, TuningManifest manifest, int groupSize, int items, bool splitPairs = true)
    {
        if (manifest.KernelAbiVersion == KernelAbiV2.Id)
        {
            List<KernelPassSpec> passes = plan.Passes.ToList();
            List<KernelBufferSpec> buffers = plan.Buffers.ToList();
            if (pairs && splitPairs)
            {
                int count = checked((int)plan.LogicalItemCount);
                uint groups = Math.Max(1u, WorkloadData.CeilDiv(count, (long)groupSize * items));
                KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(groups);
                buffers.Add(new("sorted-keys", checked(Math.Max(1, count) * 4)));
                buffers.Add(new("sorted-payloads", checked(Math.Max(1, count) * 4)));
                passes.Add(new("split-radix-pairs", "SplitRadixPairs", dispatch,
                    plan.VerifiedResource, null, "sorted-keys", "sorted-payloads",
                    [(uint)count, (uint)(groupSize * items), 0, 0, 0, 0, dispatch.X, groups]));
                plan = plan with
                {
                    VerifiedResource = "sorted-keys", ExpectedSha256 = expectedKeysHash!,
                    AdditionalVerifiedOutputs = [new("sorted-payloads", expectedPayloadsHash!)]
                };
            }
            for (int index = 1; index < passes.Count; index++)
                passes[index] = passes[index] with { DependsOn = [passes[index - 1].Name] };
            plan = plan with { AbiVersion = KernelAbiV2.Id, Buffers = buffers, Passes = passes };
        }
        plan.Validate();
        return plan;
    }
}
