namespace HlslPerf.Core;

/// <summary>
/// Stable engine-neutral ABI used by the workload pack and backend adapters.
/// HLSL bindings are t0/t1, u0/u1 and eight 32-bit root constants at b0.
/// </summary>
public static class KernelAbiV1
{
    public const string Id = "hlslperf.raw-buffer.v1";
    public const int RootConstantCount = 8;
    public const string HlslRootSignature =
        "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)";
}

public sealed record KernelBufferSpec(string Name, int ByteLength, byte[]? InitialData = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("Kernel buffer names cannot be empty.");
        if (ByteLength <= 0 || (ByteLength & 3) != 0)
            throw new InvalidDataException($"Kernel buffer '{Name}' must contain a positive multiple of four bytes.");
        if (InitialData is not null && InitialData.Length != ByteLength)
            throw new InvalidDataException($"Kernel buffer '{Name}' initial data does not match its declared byte length.");
    }
}

public sealed record KernelDispatch(uint X, uint Y = 1, uint Z = 1)
{
    public void Validate(string passName)
    {
        if (X is 0 or > 65_535 || Y is 0 or > 65_535 || Z is 0 or > 65_535)
            throw new InvalidDataException($"Pass '{passName}' dispatch dimensions must each be in 1..65,535.");
    }
}

public sealed record KernelPassSpec(
    string Name,
    string EntryPoint,
    KernelDispatch Dispatch,
    string? Input0,
    string? Input1,
    string? Output0,
    string? Output1,
    IReadOnlyList<uint> Constants)
{
    public void Validate(IReadOnlySet<string> resources)
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(EntryPoint))
            throw new InvalidDataException("Kernel pass names and entry points cannot be empty.");
        Dispatch.Validate(Name);
        if (Constants.Count > KernelAbiV1.RootConstantCount)
            throw new InvalidDataException($"Pass '{Name}' supplies more than {KernelAbiV1.RootConstantCount} root constants.");

        string[] inputs = new[] { Input0, Input1 }.OfType<string>().ToArray();
        string[] outputs = new[] { Output0, Output1 }.OfType<string>().ToArray();
        foreach (string resource in inputs.Concat(outputs))
            if (!resources.Contains(resource))
                throw new InvalidDataException($"Pass '{Name}' references unknown resource '{resource}'.");
        if (outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Length)
            throw new InvalidDataException($"Pass '{Name}' binds one resource to multiple UAV slots.");
        if (inputs.Intersect(outputs, StringComparer.Ordinal).Any())
            throw new InvalidDataException($"Pass '{Name}' cannot bind one resource as both SRV and UAV in ABI v1.");
    }
}

public sealed record KernelExecutionPlan(
    string WorkloadId,
    string AbiVersion,
    long LogicalItemCount,
    IReadOnlyList<KernelBufferSpec> Buffers,
    IReadOnlyList<KernelPassSpec> Passes,
    string VerifiedResource,
    string ExpectedSha256)
{
    public void Validate()
    {
        if (AbiVersion != KernelAbiV1.Id)
            throw new InvalidDataException($"Unsupported execution-plan ABI '{AbiVersion}'.");
        if (string.IsNullOrWhiteSpace(WorkloadId) || LogicalItemCount <= 0)
            throw new InvalidDataException("Execution plans require a workload id and positive logical item count.");
        if (Buffers.Count == 0 || Passes.Count == 0)
            throw new InvalidDataException("Execution plans require at least one buffer and one pass.");
        foreach (KernelBufferSpec buffer in Buffers)
            buffer.Validate();
        HashSet<string> names = Buffers.Select(buffer => buffer.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != Buffers.Count)
            throw new InvalidDataException("Execution-plan buffer names must be unique.");
        foreach (KernelPassSpec pass in Passes)
            pass.Validate(names);
        if (!names.Contains(VerifiedResource))
            throw new InvalidDataException($"Verified resource '{VerifiedResource}' is not declared.");
        if (ExpectedSha256.Length != 64 || ExpectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Expected output SHA-256 must contain 64 hexadecimal characters.");
    }
}

public interface IKernelWorkload
{
    string Id { get; }
    KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate);
}
