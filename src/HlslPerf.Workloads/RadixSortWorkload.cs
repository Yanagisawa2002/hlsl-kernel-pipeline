using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class RadixSortWorkload : IKernelWorkload
{
    public string Id => "radix-sort-u32-v1";

    private byte[]? inputData;
    private string? expectedHash;
    private int cachedCount;
    private int cachedSeed;
    private int cachedBitCount;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 9_181_321);
        int bitCount = spec.GetInt32("bitCount", 32);
        int backend = candidate.GetRequired("HLSLPERF_SCAN_BACKEND");
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int waveSize = OptionalDefine(candidate, "HLSLPERF_WAVE_SIZE", 0);

        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        if (bitCount is <= 0 or > 32)
            throw new InvalidDataException("bitCount must be in 1..32.");
        if (backend is not (1 or 2))
            throw new InvalidDataException($"Candidate '{candidate.Id}' radix scan backend must be 1 or 2.");
        if (vectorWidth is not (1 or 4))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width must be 1 or 4.");
        if (waveSize is not (0 or 32 or 64))
            throw new InvalidDataException($"Candidate '{candidate.Id}' wave size must be 0, 32, or 64.");
        if (waveSize != 0 && manifest.ShaderModel is not ("6_6" or "6_7"))
            throw new InvalidDataException($"Candidate '{candidate.Id}' fixed wave size requires shader model 6_6+.");

        EnsureOracle(elementCount, seed, bitCount);
        long blockSize = checked((long)groupSize * elementsPerThread);
        uint logicalGroups = WorkloadData.CeilDiv(elementCount, blockSize);
        KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(logicalGroups);
        List<KernelBufferSpec> buffers =
        [
            new("input", checked(elementCount * sizeof(uint)), inputData),
            new("radix-a", checked(elementCount * sizeof(uint))),
            new("radix-b", checked(elementCount * sizeof(uint))),
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
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed, int bitCount)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed &&
            cachedBitCount == bitCount)
            return;

        uint[] input = WorkloadData.AsUInt32(WorkloadData.GenerateUInt32(elementCount, seed));
        if (bitCount < 32)
        {
            uint mask = (1u << bitCount) - 1;
            for (int index = 0; index < input.Length; ++index)
                input[index] &= mask;
        }
        inputData = WorkloadData.ToBytes(input);
        uint[] expected = input.ToArray();
        Array.Sort(expected);
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes(expected));
        cachedCount = elementCount;
        cachedSeed = seed;
        cachedBitCount = bitCount;
    }

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;
}
