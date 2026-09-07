using System.Runtime.InteropServices;
using HlslPerf.Core;

namespace HlslPerf.Workloads;

/// <summary>Unsigned ascending low-bit sort; equal selected keys preserve input record order.</summary>
public static class RadixSortContract
{
    public static void ValidateDimensions(int count, int bitCount, bool pairs)
    {
        if (count < 0 || bitCount is < 1 or > 32)
            throw new InvalidDataException("Radix requires a nonnegative count and 1..32 key bits.");
        if (count > int.MaxValue / (pairs ? 8 : 4))
            throw new InvalidDataException("Radix record byte length exceeds the signed 32-bit buffer ABI.");
    }

    public static byte[] Pack(ReadOnlySpan<uint> keys, uint[]? payloads = null)
    {
        ValidateDimensions(keys.Length, 32, payloads is not null);
        if (payloads is not null && keys.Length != payloads.Length)
            throw new ArgumentException("Each key requires one payload.", nameof(payloads));
        uint[] records = new uint[Math.Max(1, keys.Length) * (payloads is null ? 1 : 2)];
        for (int index = 0; index < keys.Length; index++)
        {
            records[index * (payloads is null ? 1 : 2)] = keys[index];
            if (payloads is not null) records[index * 2 + 1] = payloads[index];
        }
        return MemoryMarshal.AsBytes(records.AsSpan()).ToArray();
    }

    /// <summary>Independent comparison-sort oracle; payload values never decide tie order.</summary>
    public static byte[] StableOracle(uint[] keys, uint[]? payloads = null, int bitCount = 32)
    {
        ValidateDimensions(keys.Length, bitCount, payloads is not null);
        if (payloads is not null && keys.Length != payloads.Length)
            throw new ArgumentException("Each key requires one payload.", nameof(payloads));
        uint mask = bitCount == 32 ? uint.MaxValue : (1u << bitCount) - 1;
        int[] order = Enumerable.Range(0, keys.Length).ToArray();
        Array.Sort(order, (left, right) =>
        {
            int compare = (keys[left] & mask).CompareTo(keys[right] & mask);
            return compare != 0 ? compare : left.CompareTo(right);
        });
        return Pack(order.Select(index => keys[index]).ToArray(),
            payloads is null ? null : order.Select(index => payloads[index]).ToArray());
    }

    /// <summary>Allocated scratch includes the alternate record buffer, excludes input and final output.</summary>
    public static RadixPlanCost Describe(KernelExecutionPlan plan, int bitCount, int radixBits) => new(
        (bitCount + radixBits - 1) / radixBits, plan.Passes.Count,
        plan.Buffers.Sum(buffer => (long)buffer.ByteLength),
        plan.Buffers.Where(buffer => buffer.InitialData is null && !plan.GetVerifiedOutputs().Any(output => output.Resource == buffer.Name))
            .Sum(buffer => (long)buffer.ByteLength),
        "GPU timestamp surrounds every pass and resource transition in the complete plan; upload/readback excluded.");
}

public sealed record RadixPlanCost(int DigitPasses, int DispatchPasses, long AllocatedBytes, long ScratchBytes, string TimingScope);
