using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class ScanWorkload : IKernelWorkload
{
    private readonly string id;

    public ScanWorkload(string id = "exclusive-scan-u32-v1") => this.id = id;

    public string Id => id;

    private byte[]? inputData;
    private int cachedCount;
    private int cachedSeed;
    private int cachedOperator;
    private string? expectedHash;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int elementCount = spec.GetRequiredInt32("elementCount");
        int seed = spec.GetInt32("seed", 1_337_031);
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
        int backend = candidate.Defines.TryGetValue("HLSLPERF_SCAN_BACKEND", out int selectedBackend)
            ? selectedBackend
            : 1;
        int scanOperator = OptionalDefine(candidate, "HLSLPERF_SCAN_OPERATOR", 1);
        int vectorWidth = OptionalDefine(candidate, "HLSLPERF_VECTOR_WIDTH", 1);
        int waveSize = OptionalDefine(candidate, "HLSLPERF_WAVE_SIZE", 0);
        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        if (scanOperator is < 1 or > 4)
            throw new InvalidDataException($"Candidate '{candidate.Id}' scan operator must be in 1..4.");
        if (vectorWidth is not (1 or 4))
            throw new InvalidDataException($"Candidate '{candidate.Id}' vector width must be 1 or 4.");
        if (waveSize is not (0 or 32 or 64))
            throw new InvalidDataException($"Candidate '{candidate.Id}' wave size must be 0, 32, or 64.");
        if (waveSize != 0 && manifest.ShaderModel is not ("6_6" or "6_7"))
            throw new InvalidDataException($"Candidate '{candidate.Id}' fixed wave size requires shader model 6_6+.");
        EnsureOracle(elementCount, seed, scanOperator);

        long blockSize = checked((long)groupSize * elementsPerThread);
        if (backend == 3)
        {
            long singlePassBlockSize = checked(
                (long)groupSize * elementsPerThread * GetSinglePassItemsScale(candidate));
            return BuildSinglePass(candidate, elementCount, singlePassBlockSize);
        }

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
            KernelDispatch dispatch = DispatchForGroups(groups);
            string output = $"scan-{level}";
            string sums = $"sums-{level}";
            buffers.Add(new KernelBufferSpec(output, checked(count * sizeof(uint))));
            buffers.Add(new KernelBufferSpec(sums, checked((int)groups * sizeof(uint))));
            passes.Add(new KernelPassSpec(
                $"scan-level-{level}",
                "BlockScanPass",
                dispatch,
                input,
                null,
                output,
                sums,
                [(uint)count, 0, 0, 0, 0, 0, dispatch.X, groups]));
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
            KernelDispatch dispatch = DispatchForGroups(levelGroups[childLevel]);
            passes.Add(new KernelPassSpec(
                $"add-offsets-level-{childLevel}",
                "AddScanOffsets",
                dispatch,
                $"scan-{childLevel + 1}",
                null,
                $"scan-{childLevel}",
                null,
                [
                    (uint)levelCounts[childLevel], checked((uint)blockSize), 0, 0, 0, 0,
                    dispatch.X, levelGroups[childLevel]
                ]));
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

    private KernelExecutionPlan BuildSinglePass(KernelCandidate candidate, int elementCount, long blockSize)
    {
        uint logicalBlocks = WorkloadData.CeilDiv(elementCount, blockSize);
        uint persistentGroups = Math.Min(logicalBlocks, checked((uint)GetPersistentGroupLimit(candidate)));
        int stateBytes = checked(8 + checked((int)logicalBlocks) * 12);
        List<KernelBufferSpec> buffers =
        [
            new("input", checked(elementCount * sizeof(uint)), inputData),
            new("scan-0", checked(elementCount * sizeof(uint))),
            new("single-pass-state", stateBytes, new byte[stateBytes])
        ];
        List<KernelPassSpec> passes =
        [
            new(
                "single-pass-reset",
                "ResetSinglePassState",
                new KernelDispatch(1),
                null,
                null,
                "single-pass-state",
                null,
                []),
            new(
                "single-pass-scan",
                "SinglePassScan",
                new KernelDispatch(persistentGroups),
                "input",
                null,
                "scan-0",
                "single-pass-state",
                [(uint)elementCount, checked((uint)blockSize), logicalBlocks])
        ];
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

    private static int GetPersistentGroupLimit(KernelCandidate candidate)
    {
        int limit = candidate.Defines.TryGetValue("HLSLPERF_SINGLE_PASS_GROUPS", out int value) ? value : 256;
        if (limit is <= 0 or > 65_535)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' persistent group limit must be in 1..65,535.");
        return limit;
    }

    private static int OptionalDefine(KernelCandidate candidate, string name, int fallback) =>
        candidate.Defines.TryGetValue(name, out int value) ? value : fallback;

    private static int GetSinglePassItemsScale(KernelCandidate candidate)
    {
        int scale = candidate.Defines.TryGetValue("HLSLPERF_SINGLE_PASS_ITEMS_SCALE", out int value) ? value : 1;
        if (scale is <= 0 or > 64)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' single-pass items scale must be in 1..64.");
        return scale;
    }

    private static KernelDispatch DispatchForGroups(uint groupCount)
    {
        uint x = Math.Min(65_535u, groupCount);
        uint y = (groupCount + x - 1) / x;
        return new KernelDispatch(x, y);
    }

    private void EnsureOracle(int elementCount, int seed, int scanOperator)
    {
        if (inputData is not null && cachedCount == elementCount && cachedSeed == seed &&
            cachedOperator == scanOperator)
            return;
        inputData = WorkloadData.GenerateUInt32(elementCount, seed);
        uint[] input = WorkloadData.AsUInt32(inputData);
        uint[] expected = new uint[input.Length];
        uint prefix = Identity(scanOperator);
        for (int index = 0; index < input.Length; ++index)
        {
            expected[index] = prefix;
            prefix = Combine(prefix, input[index], scanOperator);
        }
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes(expected));
        cachedCount = elementCount;
        cachedSeed = seed;
        cachedOperator = scanOperator;
    }

    private static uint Identity(int scanOperator) => scanOperator == 2 ? uint.MaxValue : 0;

    private static uint Combine(uint left, uint right, int scanOperator) => scanOperator switch
    {
        1 => unchecked(left + right),
        2 => Math.Min(left, right),
        3 => Math.Max(left, right),
        4 => left ^ right,
        _ => throw new ArgumentOutOfRangeException(nameof(scanOperator))
    };
}
