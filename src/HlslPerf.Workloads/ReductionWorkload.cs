using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class ReductionWorkload : IKernelWorkload
{
    public string Id => "reduction-u32-v1";

    private byte[]? inputData;
    private int cachedCount;
    private int cachedSeed;
    private string? expectedHash;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 1_904_887);
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        EnsureOracle(elementCount, seed);

        long blockSize = checked((long)groupSize * elementsPerThread);
        List<KernelBufferSpec> buffers = [new("input", checked(elementCount * sizeof(uint)), inputData)];
        List<KernelPassSpec> passes = [];
        string input = "input";
        int count = elementCount;
        int level = 0;
        while (true)
        {
            uint groups = WorkloadData.CeilDiv(count, blockSize);
            string output = $"reduce-{level}";
            buffers.Add(new KernelBufferSpec(output, checked((int)groups * sizeof(uint))));
            passes.Add(new KernelPassSpec(
                $"reduce-level-{level}",
                "ReducePass",
                new KernelDispatch(groups),
                input,
                null,
                output,
                null,
                [(uint)count]));
            input = output;
            count = checked((int)groups);
            level++;
            if (groups == 1)
                break;
        }

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            buffers,
            passes,
            input,
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed)
            return;
        inputData = WorkloadData.GenerateUInt32(elementCount, seed);
        uint sum = 0;
        foreach (uint value in WorkloadData.AsUInt32(inputData))
            sum = unchecked(sum + value);
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes([sum]));
        cachedCount = elementCount;
        cachedSeed = seed;
    }
}
