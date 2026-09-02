using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal static class WorkloadPlanUtilities
{
    public static KernelDispatch DispatchForGroups(uint groupCount)
    {
        if (groupCount == 0)
            throw new InvalidDataException("A GPU plan cannot dispatch zero logical groups.");
        uint x = Math.Min(65_535u, groupCount);
        uint y = (groupCount + x - 1) / x;
        return new KernelDispatch(x, y);
    }
}

/// <summary>
/// Declares one reusable hierarchical exclusive-scan scratch layout. Passes can
/// be appended repeatedly (for example, once per radix bit) without allocating
/// a second copy of the scratch buffers.
/// </summary>
internal sealed class ScanHierarchyLayout
{
    private readonly IReadOnlyList<Level> levels;
    private readonly long blockSize;

    private ScanHierarchyLayout(IReadOnlyList<Level> levels, long blockSize)
    {
        this.levels = levels;
        this.blockSize = blockSize;
    }

    public string Output => levels[0].Output;

    public static ScanHierarchyLayout Declare(
        List<KernelBufferSpec> buffers,
        string resourcePrefix,
        int elementCount,
        long blockSize)
    {
        if (string.IsNullOrWhiteSpace(resourcePrefix))
            throw new ArgumentException("A scan scratch prefix is required.", nameof(resourcePrefix));

        List<Level> levels = [];
        int count = elementCount;
        int levelIndex = 0;
        while (true)
        {
            uint groups = WorkloadData.CeilDiv(count, blockSize);
            KernelDispatch dispatch = WorkloadPlanUtilities.DispatchForGroups(groups);
            string output = $"{resourcePrefix}-scan-{levelIndex}";
            string sums = $"{resourcePrefix}-sums-{levelIndex}";
            buffers.Add(new KernelBufferSpec(output, checked(count * sizeof(uint))));
            buffers.Add(new KernelBufferSpec(sums, checked((int)groups * sizeof(uint))));
            levels.Add(new Level(count, groups, dispatch, output, sums));
            if (groups == 1)
                break;
            count = checked((int)groups);
            levelIndex++;
        }

        return new ScanHierarchyLayout(levels, blockSize);
    }

    public void AppendPasses(List<KernelPassSpec> passes, string input, string passPrefix)
    {
        string levelInput = input;
        for (int levelIndex = 0; levelIndex < levels.Count; ++levelIndex)
        {
            Level level = levels[levelIndex];
            passes.Add(new KernelPassSpec(
                $"{passPrefix}-scan-{levelIndex}",
                "BlockScanPass",
                level.Dispatch,
                levelInput,
                null,
                level.Output,
                level.Sums,
                [(uint)level.Count, 0, 0, 0, 0, 0, level.Dispatch.X, level.Groups]));
            levelInput = level.Sums;
        }

        for (int childLevel = levels.Count - 2; childLevel >= 0; --childLevel)
        {
            Level level = levels[childLevel];
            passes.Add(new KernelPassSpec(
                $"{passPrefix}-offsets-{childLevel}",
                "AddScanOffsets",
                level.Dispatch,
                levels[childLevel + 1].Output,
                null,
                level.Output,
                null,
                [
                    (uint)level.Count, checked((uint)blockSize), 0, 0, 0, 0,
                    level.Dispatch.X, level.Groups
                ]));
        }
    }

    private sealed record Level(
        int Count,
        uint Groups,
        KernelDispatch Dispatch,
        string Output,
        string Sums);
}
