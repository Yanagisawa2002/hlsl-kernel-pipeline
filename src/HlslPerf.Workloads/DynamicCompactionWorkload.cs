using HlslPerf.Core;

namespace HlslPerf.Workloads;

/// <summary>A correctness demo: stable GPU compaction followed by bounded indirect transform/binning.</summary>
public sealed class DynamicCompactionWorkload : IKernelWorkload
{
    public string Id => "dynamic-compaction-consume-u32-v2";
    public const int MaximumElementCount = 65_535;

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        WorkloadSpec spec = manifest.Workload!;
        int count = spec.GetRequiredInt32("elementCount");
        int mode = spec.GetInt32("activeMode", 1); // 0=none, 1=predicate, 2=all
        int limit = spec.GetInt32("maximumItems", count);
        int countOffset = spec.GetInt32("countByteOffset", 0);
        int argumentOffset = spec.GetInt32("argumentByteOffset", 0);
        int seed = spec.GetInt32("seed", 19);
        bool overflowProbe = spec.GetInt32("overflowProbe", 0) != 0;
        int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
        if (count is < 0 or > MaximumElementCount || limit < 0 || limit > count || mode is < 0 or > 2 ||
            countOffset is < 0 or > 64 || argumentOffset is < 0 or > 64 ||
            (countOffset & 3) != 0 || (argumentOffset & 3) != 0 || groupSize is not (1 or 64) ||
            (overflowProbe && mode != 2))
            throw new InvalidDataException("Dynamic demo parameters exceed declared count, mode, offset or group bounds.");
        uint[] input = count == 0 ? [0] : WorkloadData.AsUInt32(WorkloadData.GenerateUInt32(count, seed));
        // CPU oracle uses a separate list/filter implementation; never interprets GPU scratch.
        uint[] selected = input.Take(count).Where(value => mode == 2 || (mode == 1 && (value & 3) == 0)).ToArray();
        uint rawCount = overflowProbe ? uint.MaxValue : checked((uint)selected.Length);
        int active = checked((int)Math.Min(rawCount, (uint)limit));
        uint[] compacted = new uint[Math.Max(1, count)];
        selected.CopyTo(compacted, 0);
        uint[] consumed = new uint[Math.Max(1, limit)];
        uint[] bins = new uint[8];
        for (int index = 0; index < active; ++index)
        {
            uint value = selected[index];
            consumed[index] = unchecked(value * 17u + (uint)index);
            bins[value % 8]++;
        }
        uint[] metadata = new uint[countOffset / 4 + 2];
        metadata[countOffset / 4] = rawCount;
        uint[] arguments = new uint[argumentOffset / 4 + 4];
        arguments[argumentOffset / 4] = checked((uint)((active + groupSize - 1) / groupSize));
        arguments[argumentOffset / 4 + 1] = arguments[argumentOffset / 4 + 2] = 1;
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["compacted"] = WorkloadData.ToBytes(compacted), ["consumed"] = WorkloadData.ToBytes(consumed),
            ["bins"] = WorkloadData.ToBytes(bins), ["count"] = WorkloadData.ToBytes(metadata),
            ["arguments"] = WorkloadData.ToBytes(arguments)
        };
        List<KernelBufferSpec> buffers = [new("input", input.Length * 4, WorkloadData.ToBytes(input))];
        buffers.AddRange(expected.Select(pair => new KernelBufferSpec(pair.Key, pair.Value.Length)));
        uint[] constants = [(uint)count, (uint)mode, (uint)limit, (uint)countOffset, (uint)metadata.Length,
            overflowProbe ? 1u : 0u, 0, 0];
        KernelExecutionPlan plan = new(Id, KernelAbiV2.Id, count, buffers,
        [
            new("reset-consumer", "ResetConsumer", new((uint)((Math.Max(8, limit) + 63) / 64)),
                null, null, "consumed", "bins", constants),
            new("compact-and-count", "CompactAndCount", new(1), "input", null, "compacted", "count", constants)
                { DependsOn = ["reset-consumer"] },
            new("consume-and-bin", "ConsumeAndBin", new(1), "compacted", "count", "consumed", "bins", constants)
            {
                DependsOn = ["compact-and-count"],
                Indirect = new("count", countOffset, "arguments", argumentOffset, (uint)limit, (uint)groupSize)
            }
        ], "consumed", ContentHash.Sha256(expected["consumed"]))
        {
            AdditionalVerifiedOutputs = expected.Where(pair => pair.Key != "consumed")
                .Select(pair => new KernelVerifiedOutput(pair.Key, ContentHash.Sha256(pair.Value))).ToArray()
        };
        plan.Validate();
        return plan;
    }
}
