using System.Numerics;
using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class HistogramPrefixWorkload : IKernelWorkload
{
    public string Id => "histogram-prefix-u32-v1";

    private byte[]? inputData;
    private string? expectedHash;
    private int cachedCount;
    private int cachedSeed;
    private int cachedBinCount;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 7_117_013);
        int binCount = spec.GetRequiredInt32("binCount");
        int compiledBinCount = candidate.GetRequired("HLSLPERF_HISTOGRAM_BINS");
        int backend = candidate.GetRequired("HLSLPERF_HISTOGRAM_BACKEND");
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int replicas = OptionalDefine(candidate, "HLSLPERF_HISTOGRAM_REPLICAS", 1);

        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        if (binCount != compiledBinCount || binCount is <= 1 or > 1024 || !BitOperations.IsPow2((uint)binCount))
            throw new InvalidDataException($"Candidate '{candidate.Id}' histogram bins must match a power-of-two workload binCount in 2..1024.");
        if (backend is not (1 or 2))
            throw new InvalidDataException($"Candidate '{candidate.Id}' histogram backend must be 1 or 2.");
        if (vectorWidth is not (1 or 4))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width must be 1 or 4.");
        if (replicas is <= 0 or > 8 || !BitOperations.IsPow2((uint)replicas))
            throw new InvalidDataException($"Candidate '{candidate.Id}' histogram replicas must be a power of two in 1..8.");
        int groupsharedBytes = checked((binCount * replicas + binCount + 1) * sizeof(uint));
        if (groupsharedBytes > 32 * 1024)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' requests {groupsharedBytes} bytes, above the workload pack's 32 KiB LDS budget.");

        EnsureOracle(elementCount, seed, binCount);
        long blockSize = checked((long)groupSize * elementsPerThread);
        uint logicalGroups = WorkloadData.CeilDiv(elementCount, blockSize);
        KernelDispatch histogramDispatch = WorkloadPlanUtilities.DispatchForGroups(logicalGroups);

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            [
                new("input", checked(elementCount * sizeof(uint)), inputData),
                new("histogram", checked(binCount * sizeof(uint))),
                new("offsets", checked((binCount + 1) * sizeof(uint)))
            ],
            [
                new(
                    "histogram-reset",
                    "ResetHistogram",
                    new KernelDispatch(1),
                    null,
                    null,
                    "histogram",
                    null,
                    [0, (uint)binCount]),
                new(
                    "histogram-build",
                    "BuildHistogram",
                    histogramDispatch,
                    "input",
                    null,
                    "histogram",
                    null,
                    [
                        (uint)elementCount, (uint)binCount, checked((uint)blockSize), logicalGroups,
                        histogramDispatch.X
                    ]),
                new(
                    "histogram-prefix",
                    "PrefixHistogram",
                    new KernelDispatch(1),
                    "histogram",
                    null,
                    "offsets",
                    null,
                    [(uint)elementCount, (uint)binCount])
            ],
            "offsets",
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed, int binCount)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed &&
            cachedBinCount == binCount)
            return;

        inputData = WorkloadData.GenerateUInt32(elementCount, seed);
        uint[] histogram = new uint[binCount];
        foreach (uint value in WorkloadData.AsUInt32(inputData))
            histogram[value & checked((uint)(binCount - 1))]++;
        uint[] offsets = new uint[binCount + 1];
        for (int bin = 0; bin < binCount; ++bin)
            offsets[bin + 1] = checked(offsets[bin] + histogram[bin]);
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes(offsets));
        cachedCount = elementCount;
        cachedSeed = seed;
        cachedBinCount = binCount;
    }

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;
}
