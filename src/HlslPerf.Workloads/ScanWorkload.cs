using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class ScanWorkload : IKernelWorkload
{
    public string Id => "exclusive-scan-u32-v1";

    private byte[]? inputData;
    private int cachedCount;
    private int cachedSeed;
    private string? expectedHash;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 1_337_031);
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        EnsureOracle(elementCount, seed);

        long blockSize = checked((long)groupSize * elementsPerThread);
        List<KernelBufferSpec> buffers = [new("input", checked(elementCount * sizeof(uint)), inputData)];
        List<KernelPassSpec> passes = [];
        List<int> levelCounts = [];
        List<uint> levelGroups = [];
        string input = "input";
        int count = elementCount;
        int level = 0;
        while (true)
        {
            uint groups = WorkloadData.CeilDiv(count, blockSize);
            string output = $"scan-{level}";
            string sums = $"sums-{level}";
            buffers.Add(new KernelBufferSpec(output, checked(count * sizeof(uint))));
            buffers.Add(new KernelBufferSpec(sums, checked((int)groups * sizeof(uint))));
            passes.Add(new KernelPassSpec(
                $"scan-level-{level}",
                "BlockScanPass",
                new KernelDispatch(groups),
                input,
                null,
                output,
                sums,
                [(uint)count]));
            levelCounts.Add(count);
            levelGroups.Add(groups);
            if (groups == 1)
                break;
            input = sums;
            count = checked((int)groups);
            level++;
        }

        for (int childLevel = levelCounts.Count - 2; childLevel >= 0; --childLevel)
        {
            passes.Add(new KernelPassSpec(
                $"add-offsets-level-{childLevel}",
                "AddScanOffsets",
                new KernelDispatch(levelGroups[childLevel]),
                $"scan-{childLevel + 1}",
                null,
                $"scan-{childLevel}",
                null,
                [(uint)levelCounts[childLevel], checked((uint)blockSize)]));
        }

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            buffers,
            passes,
            "scan-0",
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed)
            return;
        inputData = WorkloadData.GenerateUInt32(elementCount, seed);
        uint[] input = WorkloadData.AsUInt32(inputData);
        uint[] expected = new uint[input.Length];
        uint prefix = 0;
        for (int index = 0; index < input.Length; ++index)
        {
            expected[index] = prefix;
            prefix = unchecked(prefix + input[index]);
        }
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes(expected));
        cachedCount = elementCount;
        cachedSeed = seed;
    }
}
