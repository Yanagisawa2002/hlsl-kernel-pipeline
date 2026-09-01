using System.Runtime.InteropServices;
using HlslPerf.Core;

namespace HlslPerf.Workloads;

internal sealed class UintMixWorkload : IKernelWorkload
{
    public string Id => "uint-mix-v1";

    public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
    {
        int groupSize = candidate.GetRequired(manifest.ThreadsPerGroupParameter);
        int elementsPerThread = candidate.GetRequired(manifest.ElementsPerThreadParameter);
        WorkloadData.ValidatePowerOfTwoGroup(groupSize, candidate.Id);
        int rounds = manifest.FixedDefines.TryGetValue("HLSLPERF_ALU_ROUNDS", out int value) ? value : 64;
        long blockSize = checked((long)groupSize * elementsPerThread);
        uint groups = WorkloadData.CeilDiv(manifest.WorkItemCount, blockSize);

        uint[] expected = new uint[manifest.WorkItemCount];
        uint seed = unchecked((uint)manifest.Correctness.Seed);
        Parallel.For(0, expected.Length, index =>
        {
            uint mixed = (uint)index ^ seed;
            for (uint round = 0; round < rounds; ++round)
            {
                mixed ^= mixed << 13;
                mixed ^= mixed >> 17;
                mixed ^= mixed << 5;
                mixed = unchecked(mixed * 1_664_525u + 1_013_904_223u + round);
            }
            expected[index] = mixed;
        });

        KernelExecutionPlan plan = new(
            Id,
            KernelAbiV1.Id,
            manifest.WorkItemCount,
            [new KernelBufferSpec("output", checked(manifest.WorkItemCount * sizeof(uint)))],
            [new KernelPassSpec(
                "uint-mix",
                manifest.EntryPoint,
                new KernelDispatch(groups),
                null,
                null,
                "output",
                null,
                [(uint)manifest.WorkItemCount, seed])],
            "output",
            ContentHash.Sha256(MemoryMarshal.AsBytes(expected.AsSpan())));
        plan.Validate();
        return plan;
    }
}
