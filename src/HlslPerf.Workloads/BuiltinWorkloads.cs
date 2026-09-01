using System.Runtime.InteropServices;
using System.Numerics;
using HlslPerf.Core;

namespace HlslPerf.Workloads;

public static class BuiltinWorkloads
{
    public static IKernelWorkload Resolve(TuningManifest manifest)
    {
        string id = manifest.SchemaVersion == "1.0"
            ? manifest.Correctness.Kind
            : manifest.Workload?.Id ?? throw new InvalidDataException("A schema 2.0 workload is required.");
        return id switch
        {
            "uint-mix-v1" or "cross-candidate-sha256" => new UintMixWorkload(),
            "reduction-u32-v1" => new ReductionWorkload(),
            "exclusive-scan-u32-v1" => new ScanWorkload(),
            "transpose-u32-v1" => new TransposeWorkload(),
            _ => throw new InvalidDataException($"Unknown built-in workload '{id}'.")
        };
    }
}

internal static class WorkloadData
{
    public static byte[] GenerateUInt32(int count, int seed)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("The built-in uint workloads require a little-endian CPU.");
        uint[] values = new uint[count];
        uint state = unchecked((uint)seed);
        for (int index = 0; index < values.Length; ++index)
        {
            state = unchecked(state * 1_664_525u + 1_013_904_223u);
            uint mixed = state ^ (state >> 16) ^ unchecked((uint)index * 2_246_822_519u);
            values[index] = mixed;
        }
        return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }

    public static uint[] AsUInt32(byte[] data) => MemoryMarshal.Cast<byte, uint>(data).ToArray();

    public static byte[] ToBytes(ReadOnlySpan<uint> values) => MemoryMarshal.AsBytes(values).ToArray();

    public static uint CeilDiv(int value, long divisor) => checked((uint)((value + divisor - 1) / divisor));

    public static void ValidatePowerOfTwoGroup(int groupSize, string candidateId)
    {
        if (groupSize is <= 0 or > 1024 || !BitOperations.IsPow2((uint)groupSize))
            throw new InvalidDataException($"Candidate '{candidateId}' group size must be a power of two in 1..1024.");
    }
}
