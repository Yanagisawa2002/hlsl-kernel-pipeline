using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class SegmentedScanWorkload : IKernelWorkload
{
    public string Id => "segmented-exclusive-scan-u32-v1";

    private byte[]? inputData;
    private byte[]? headData;
    private string? expectedHash;
    private int cachedCount;
    private int cachedSeed;
    private int cachedAverageSegmentLength;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 6_941_027);
        int averageSegmentLength = spec.GetInt32("averageSegmentLength", 64);
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int itemScale = OptionalDefine(candidate, "HLSLPERF_ITEMS_SCALE", 1);
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int waveSize = OptionalDefine(candidate, "HLSLPERF_WAVE_SIZE", 0);
        int persistentLimit = OptionalDefine(candidate, "HLSLPERF_PERSISTENT_GROUPS", 256);

        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        if (itemScale is <= 0 or > 16)
            throw new InvalidDataException($"Candidate '{candidate.Id}' item scale must be in 1..16.");
        if (vectorWidth is not (1 or 4))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width must be 1 or 4.");
        if (waveSize is not (0 or 32 or 64))
            throw new InvalidDataException($"Candidate '{candidate.Id}' wave size must be 0, 32, or 64.");
        if (waveSize != 0 && manifest.ShaderModel is not ("6_6" or "6_7"))
            throw new InvalidDataException($"Candidate '{candidate.Id}' fixed wave size requires shader model 6_6+.");
        if (persistentLimit is <= 0 or > 65_535)
            throw new InvalidDataException($"Candidate '{candidate.Id}' persistent group limit must be in 1..65,535.");
        if (averageSegmentLength <= 1)
            throw new InvalidDataException("averageSegmentLength must be greater than one.");

        EnsureOracle(elementCount, seed, averageSegmentLength);
        long blockSize = checked((long)groupSize * elementsPerThread * itemScale);
        uint logicalBlocks = WorkloadData.CeilDiv(elementCount, blockSize);
        uint persistentGroups = Math.Min(logicalBlocks, checked((uint)persistentLimit));
        int stateBytes = checked(8 + checked((int)logicalBlocks) * 20);

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            [
                new("values", checked(elementCount * sizeof(uint)), inputData),
                new("heads", checked(elementCount * sizeof(uint)), headData),
                new("segmented-output", checked(elementCount * sizeof(uint))),
                new("segmented-state", stateBytes, new byte[stateBytes])
            ],
            [
                new(
                    "segmented-reset",
                    "ResetSegmentedScanState",
                    new KernelDispatch(1),
                    null,
                    null,
                    null,
                    "segmented-state",
                    []),
                new(
                    "segmented-scan",
                    "SegmentedScanSinglePass",
                    new KernelDispatch(persistentGroups),
                    "values",
                    "heads",
                    "segmented-output",
                    "segmented-state",
                    [(uint)elementCount, checked((uint)blockSize), logicalBlocks])
            ],
            "segmented-output",
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed, int averageSegmentLength)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed &&
            cachedAverageSegmentLength == averageSegmentLength)
            return;

        inputData = WorkloadData.GenerateUInt32(elementCount, seed);
        uint[] input = WorkloadData.AsUInt32(inputData);
        uint[] heads = new uint[elementCount];
        uint[] expected = new uint[elementCount];
        uint running = 0;
        for (int index = 0; index < elementCount; ++index)
        {
            uint entropy = input[index] ^ unchecked((uint)seed * 2_246_822_519u) ^ unchecked((uint)index);
            bool isHead = index == 0 || entropy % (uint)averageSegmentLength == 0;
            heads[index] = isHead ? 1u : 0u;
            if (isHead)
                running = 0;
            expected[index] = running;
            running = unchecked(running + input[index]);
        }

        headData = WorkloadData.ToBytes(heads);
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes(expected));
        cachedCount = elementCount;
        cachedSeed = seed;
        cachedAverageSegmentLength = averageSegmentLength;
    }

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;
}
