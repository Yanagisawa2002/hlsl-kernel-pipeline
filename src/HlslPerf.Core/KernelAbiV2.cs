namespace HlslPerf.Core;

/// <summary>Versioned plan semantics; shader root bindings remain byte-compatible with v1.</summary>
public static class KernelAbiV2
{
    public const string Id = "hlslperf.raw-buffer.v2";
    public const string GpuScope = "complete-plan-including-reset-count-bounded-arguments-indirect-and-transitions";

    internal static void Validate(KernelExecutionPlan plan)
    {
        var buffers = plan.Buffers.ToDictionary(buffer => buffer.Name, StringComparer.Ordinal);
        var ancestors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var lastWriter = new Dictionary<string, string>(StringComparer.Ordinal);
        var readers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var arguments = plan.Passes.Where(pass => pass.Indirect is not null)
            .Select(pass => pass.Indirect!.ArgumentResource).ToHashSet(StringComparer.Ordinal);
        foreach (KernelPassSpec pass in plan.Passes)
        {
            if (ancestors.ContainsKey(pass.Name))
                throw new InvalidDataException($"Duplicate pass name '{pass.Name}'.");
            HashSet<string> dependencies = new(StringComparer.Ordinal);
            foreach (string dependency in pass.DependsOn)
            {
                if (!ancestors.TryGetValue(dependency, out var previous))
                    throw new InvalidDataException($"Pass '{pass.Name}' dependency '{dependency}' must precede it.");
                dependencies.Add(dependency);
                dependencies.UnionWith(previous);
            }
            var inputs = new[] { pass.Input0, pass.Input1 }.OfType<string>().ToHashSet(StringComparer.Ordinal);
            var outputs = new[] { pass.Output0, pass.Output1 }.OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (outputs.Overlaps(arguments))
                throw new InvalidDataException("Indirect argument buffers are exclusively written by the bounded executor setup.");
            if (pass.Indirect is { } indirect)
            {
                indirect.Validate(buffers);
                if (!lastWriter.ContainsKey(indirect.CountResource))
                    throw new InvalidDataException($"Pass '{pass.Name}' requires a GPU count producer in this plan.");
                if (outputs.Contains(indirect.CountResource) || inputs.Contains(indirect.ArgumentResource))
                    throw new InvalidDataException("Indirect count/argument buffers cannot alias consumer write/read bindings.");
                inputs.Add(indirect.CountResource);
                outputs.Add(indirect.ArgumentResource);
            }
            foreach (string resource in inputs.Concat(outputs).Distinct(StringComparer.Ordinal))
                if (lastWriter.TryGetValue(resource, out string? writer) && !dependencies.Contains(writer))
                    throw new InvalidDataException($"Pass '{pass.Name}' must depend on producer '{writer}' of '{resource}'.");
            foreach (string resource in outputs)
                if (readers.TryGetValue(resource, out var previousReaders) && previousReaders.Any(reader => !dependencies.Contains(reader)))
                    throw new InvalidDataException($"Pass '{pass.Name}' must depend on previous readers of '{resource}'.");
            foreach (string resource in inputs)
            {
                if (!lastWriter.ContainsKey(resource) && buffers[resource].InitialData is null)
                    throw new InvalidDataException($"Pass '{pass.Name}' reads uninitialized resource '{resource}'.");
                if (!readers.TryGetValue(resource, out var set)) readers[resource] = set = new(StringComparer.Ordinal);
                set.Add(pass.Name);
            }
            foreach (string resource in outputs)
            {
                lastWriter[resource] = pass.Name;
                readers.Remove(resource);
            }
            ancestors.Add(pass.Name, dependencies);
        }
        HashSet<string> verified = new(StringComparer.Ordinal);
        foreach (KernelVerifiedOutput output in plan.GetVerifiedOutputs())
        {
            if (!buffers.ContainsKey(output.Resource) || !lastWriter.ContainsKey(output.Resource) || !verified.Add(output.Resource))
                throw new InvalidDataException($"Verified output '{output.Resource}' must be unique, declared and produced by the plan.");
            if (output.ExpectedSha256.Length != 64 || output.ExpectedSha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"Output '{output.Resource}' requires a full SHA-256 oracle.");
        }
    }
}

public sealed record KernelVerifiedOutput(string Resource, string ExpectedSha256);

/// <summary>
/// The executor reads a GPU-produced uint count, clamps it, and writes one DISPATCH
/// record (X=ceil(min(count,MaximumItemCount)/ThreadsPerGroup), Y=Z=1).
/// The consumer must guard its final partial group and apply the same item cap.
/// Dispatch in KernelPassSpec is a compatibility placeholder when Indirect is set.
/// </summary>
public sealed record KernelIndirectDispatch(
    string CountResource, int CountByteOffset, string ArgumentResource, int ArgumentByteOffset,
    uint MaximumItemCount, uint ThreadsPerGroup)
{
    public const int ArgumentByteLength = 12;

    public void Validate(IReadOnlyDictionary<string, KernelBufferSpec> buffers)
    {
        if (!buffers.TryGetValue(CountResource, out var count) ||
            !buffers.TryGetValue(ArgumentResource, out var arguments) || CountResource == ArgumentResource)
            throw new InvalidDataException("Indirect count and argument buffers must be distinct declared resources.");
        if (CountByteOffset < 0 || (CountByteOffset & 3) != 0 || (long)CountByteOffset + 4 > count.ByteLength ||
            ArgumentByteOffset < 0 || (ArgumentByteOffset & 3) != 0 || (long)ArgumentByteOffset + ArgumentByteLength > arguments.ByteLength)
            throw new InvalidDataException("Indirect count/argument ranges must be uint-aligned and fit their buffers.");
        if (ThreadsPerGroup is 0 or > 1024 || MaximumItemCount > 65_535UL * ThreadsPerGroup)
            throw new InvalidDataException("Indirect item bounds must fit one-dimensional D3D12 dispatch limits.");
    }
}

public sealed record KernelOutputVerification(
    string Resource, int Attempt, byte PoisonByte, string ActualSha256, string ExpectedSha256, bool Passed);
