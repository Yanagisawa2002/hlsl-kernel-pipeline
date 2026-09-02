using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class StreamCompactionWorkload : IKernelWorkload
{
    public string Id => "stream-compaction-u32-v1";

    private byte[]? inputData;
    private byte[]? expectedOutput;
    private string? expectedHash;
    private int cachedCount;
    private int cachedSeed;
    private uint cachedMask;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 2_983_117);
        uint predicateMask = checked((uint)spec.GetInt32("predicateMask", 7));
        int backend = candidate.GetRequired("HLSLPERF_SCAN_BACKEND");
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int waveSize = OptionalDefine(candidate, "HLSLPERF_WAVE_SIZE", 0);

        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        if (backend is < 1 or > 3)
            throw new InvalidDataException($"Candidate '{candidate.Id}' compaction backend must be in 1..3.");
        if (vectorWidth is not (1 or 4))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width must be 1 or 4.");
        if (waveSize is not (0 or 32 or 64))
            throw new InvalidDataException($"Candidate '{candidate.Id}' wave size must be 0, 32, or 64.");
        if (waveSize != 0 && manifest.ShaderModel is not ("6_6" or "6_7"))
            throw new InvalidDataException($"Candidate '{candidate.Id}' fixed wave size requires shader model 6_6+.");

        EnsureOracle(elementCount, seed, predicateMask);
        List<KernelBufferSpec> buffers =
        [
            new("input", checked(elementCount * sizeof(uint)), inputData),
            new("compacted", expectedOutput!.Length)
        ];
        List<KernelPassSpec> passes = [];

        if (backend == 3)
        {
            int scale = OptionalDefine(candidate, "HLSLPERF_SINGLE_PASS_ITEMS_SCALE", 1);
            int persistentLimit = OptionalDefine(candidate, "HLSLPERF_SINGLE_PASS_GROUPS", 256);
            if (scale is <= 0 or > 16)
                throw new InvalidDataException($"Candidate '{candidate.Id}' single-pass item scale must be in 1..16.");
            if (persistentLimit is <= 0 or > 65_535)
                throw new InvalidDataException($"Candidate '{candidate.Id}' persistent group limit must be in 1..65,535.");
            long fusedBlockSize = checked((long)groupSize * elementsPerThread * scale);
            uint logicalBlocks = WorkloadData.CeilDiv(elementCount, fusedBlockSize);
            uint persistentGroups = Math.Min(logicalBlocks, checked((uint)persistentLimit));
            int stateBytes = checked(8 + checked((int)logicalBlocks) * 12);
            buffers.Add(new KernelBufferSpec("compaction-state", stateBytes, new byte[stateBytes]));
            passes.Add(new KernelPassSpec(
                "fused-reset",
                "ResetSinglePassState",
                new KernelDispatch(1),
                null,
                null,
                "compaction-state",
                null,
                []));
            passes.Add(new KernelPassSpec(
                "fused-produce-scan-scatter",
                "FusedCompactSinglePass",
                new KernelDispatch(persistentGroups),
                "input",
                null,
                "compacted",
                "compaction-state",
                [(uint)elementCount, checked((uint)fusedBlockSize), logicalBlocks, predicateMask]));
        }
        else
        {
            long blockSize = checked((long)groupSize * elementsPerThread);
            uint logicalGroups = WorkloadData.CeilDiv(elementCount, blockSize);
            KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(logicalGroups);
            buffers.Add(new KernelBufferSpec("flags", checked(elementCount * sizeof(uint))));
            ScanHierarchyLayout scan = ScanHierarchyLayout.Declare(
                buffers,
                "compaction",
                elementCount,
                blockSize);
            passes.Add(new KernelPassSpec(
                "produce-flags",
                "ProduceCompactionFlags",
                dispatch,
                "input",
                null,
                "flags",
                null,
                [(uint)elementCount, checked((uint)blockSize), 0, predicateMask, 0, 0, dispatch.X, logicalGroups]));
            scan.AppendPasses(passes, "flags", "compaction");
            passes.Add(new KernelPassSpec(
                "scatter",
                "ScatterCompactedValues",
                dispatch,
                "input",
                scan.Output,
                "compacted",
                null,
                [(uint)elementCount, checked((uint)blockSize), 0, predicateMask, 0, 0, dispatch.X, logicalGroups]));
        }

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            buffers,
            passes,
            "compacted",
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int elementCount, int seed, uint predicateMask)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed &&
            cachedMask == predicateMask)
            return;

        inputData = WorkloadData.GenerateUInt32(elementCount, seed);
        uint[] input = WorkloadData.AsUInt32(inputData);
        List<uint> selected = [];
        foreach (uint value in input)
            if ((value & predicateMask) == 0)
                selected.Add(value);
        uint[] expected = new uint[selected.Count + 1];
        expected[0] = checked((uint)selected.Count);
        selected.CopyTo(expected, 1);
        expectedOutput = WorkloadData.ToBytes(expected);
        expectedHash = ContentHash.Sha256(expectedOutput);
        cachedCount = elementCount;
        cachedSeed = seed;
        cachedMask = predicateMask;
    }

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;
}
