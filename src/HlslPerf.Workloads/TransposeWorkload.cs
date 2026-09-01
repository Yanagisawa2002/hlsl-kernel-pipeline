using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class TransposeWorkload : IKernelWorkload
{
    public string Id => "transpose-u32-v1";

    private byte[]? inputData;
    private int cachedWidth;
    private int cachedHeight;
    private int cachedSeed;
    private string? expectedHash;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int width = spec.GetRequiredInt32("width");
        int height = spec.GetRequiredInt32("height");
        int seed = spec.GetInt32("seed", 9_120_241);
        int tileDimension = candidate.GetRequired("HLSLPERF_TILE_DIM");
        int blockRows = candidate.GetRequired("HLSLPERF_BLOCK_ROWS");
        if (tileDimension is <= 0 or > 32 || blockRows is <= 0 or > 32 ||
            checked(tileDimension * blockRows) > 1024)
            throw new InvalidDataException(
                $"Candidate '{candidate.Id}' requires TILE_DIM/BLOCK_ROWS in 1..32 and at most 1024 threads.");
        EnsureOracle(width, height, seed);

        int elementCount = checked(width * height);
        uint groupsX = WorkloadData.CeilDiv(width, tileDimension);
        uint groupsY = WorkloadData.CeilDiv(height, tileDimension);
        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            elementCount,
            [
                new KernelBufferSpec("input", checked(elementCount * sizeof(uint)), inputData),
                new KernelBufferSpec("output", checked(elementCount * sizeof(uint)))
            ],
            [new KernelPassSpec(
                "tiled-transpose",
                "TransposePass",
                new KernelDispatch(groupsX, groupsY),
                "input",
                null,
                "output",
                null,
                [(uint)width, (uint)height])],
            "output",
            expectedHash!);
        plan.Validate();
        return plan;
    }

    private void EnsureOracle(int width, int height, int seed)
    {
        if (inputData is not null && cachedWidth == width && cachedHeight == height && cachedSeed == seed)
            return;
        int count = checked(width * height);
        inputData = WorkloadData.GenerateUInt32(count, seed);
        uint[] input = WorkloadData.AsUInt32(inputData);
        uint[] expected = new uint[count];
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
                expected[x * height + y] = input[y * width + x];
        expectedHash = ContentHash.Sha256(WorkloadData.ToBytes(expected));
        cachedWidth = width;
        cachedHeight = height;
        cachedSeed = seed;
    }
}
