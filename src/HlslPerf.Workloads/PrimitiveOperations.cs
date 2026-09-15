using HlslPerf.Core;
using System.Text.Json;

namespace HlslPerf.Workloads;

public enum ScanImplementation { InternalBaseline, GpuPrefixSumsReduceThenScan, WaveTiled }
public enum SortImplementation { InternalBinary, AmdParallelSort }

/// <summary>Explicit SDK operation construction. Never measures, tunes, or creates a device.</summary>
public static class PrimitiveOperations
{
    public static UnifiedOperationPlan ExclusiveScan(string assetRoot, ReadOnlySpan<uint> input,
        ScanImplementation implementation = ScanImplementation.InternalBaseline) =>
        implementation == ScanImplementation.WaveTiled ? WaveTiledScan(assetRoot, input, false) :
        UnifiedWorkloads.Build(assetRoot, UnifiedWorkloads.Fixture("scan", input.Length, 0,
            explicitInput: input.ToArray()), implementation switch
        {
            ScanImplementation.InternalBaseline => "internal-scan-baseline",
            ScanImplementation.GpuPrefixSumsReduceThenScan => "gps-reduce-then-scan",
            _ => throw new ArgumentOutOfRangeException(nameof(implementation))
        });

    /// <summary>Explicit SM6.6 / wave32 local adaptation with inclusive uint32 sums
    /// modulo 2^32. Reset and scan are both part of every nonempty operation.</summary>
    public static UnifiedOperationPlan InclusiveScan(string assetRoot, ReadOnlySpan<uint> input) =>
        WaveTiledScan(assetRoot, input, true);

    private static UnifiedOperationPlan WaveTiledScan(string assetRoot, ReadOnlySpan<uint> input, bool inclusive)
    {
        string root = Path.GetFullPath(assetRoot);
        int count = input.Length;
        byte[] data = count == 0 ? new byte[4] : WorkloadData.ToBytes(input);
        uint[] expected = new uint[Math.Max(1, count)];
        uint sum = 0;
        for (int i = 0; i < count; ++i)
        {
            if (!inclusive) expected[i] = sum;
            sum = unchecked(sum + input[i]);
            if (inclusive) expected[i] = sum;
        }
        string semantic = (inclusive ? "inclusive" : "exclusive") + "-u32-sum-modulo-2^32";
        string implementation = "wave-tiled-" + (inclusive ? "inclusive" : "exclusive");
        string resetId = implementation + "/" + WaveTiledScanCandidates.ResetEntryPoint;
        string scanId = implementation + "/" + WaveTiledScanCandidates.ScanEntryPoint;
        var candidate = inclusive ? WaveTiledScanCandidates.CreateInclusive() : WaveTiledScanCandidates.Create();
        WaveTiledScanCandidates.Validate(candidate, allowInclusive: inclusive);
        int blocks = checked((int)(((long)count + 4095) / 4096));
        var defines = candidate.Defines.ToDictionary(p => p.Key, p => p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UnifiedShader[] shaders = count == 0 ? [] : new[] { WaveTiledScanCandidates.ResetEntryPoint, WaveTiledScanCandidates.ScanEntryPoint }
            .Select(entry => new UnifiedShader(implementation + "/" + entry, Path.Combine(root, "kernels/scan.hlsl"), entry, defines,
                [Path.Combine(root, "kernels")], [])).ToArray();
        // Match the established SDK's empty guard: a four-byte zero output and
        // one copy, with no shader dispatch or scratch reset. Logical count is zero.
        UnifiedPass[] passes = count == 0 ?
            [new("empty-guard", UnifiedStage.InputRestore, null, null, [], [], [])
                { CopySource = "input", CopyDestination = "output", CopyBytes = 4 }] :
            [new("reset", UnifiedStage.ScratchInitialization, resetId,
                new(1), [], ["state"], [0, 0, (uint)blocks]),
             new("scan", UnifiedStage.Algorithm, scanId,
                new((uint)Math.Min(blocks, 256)), ["input"], ["output", "state"], [(uint)count, 4096, (uint)blocks])];
        UnifiedOperationPlan plan = new(implementation, count, semantic, ContentHash.Sha256(data),
            [new("input", data.Length, data), new("output", checked(expected.Length * 4)),
             new("state", checked(8 + 12 * blocks))], shaders, passes,
            [new("output", ContentHash.Sha256(WorkloadData.ToBytes(expected)))]) { ImmutableInputs = ["input"] };
        plan.Validate();
        return plan;
    }

    public static UnifiedOperationPlan StableSort(string assetRoot, ReadOnlySpan<uint> keys,
        uint[]? payloads = null, SortImplementation implementation = SortImplementation.InternalBinary)
    {
        if (payloads is not null && payloads.Length != keys.Length)
            throw new ArgumentException("Every key must have a payload.", nameof(payloads));
        uint[] values = keys.ToArray();
        var fixture = UnifiedWorkloads.Fixture("radix", values.Length, 0, pairs: payloads is not null, explicitInput: values);
        if (payloads is not null)
        {
            uint[] sorted = WorkloadData.AsUInt32(RadixSortContract.StableOracle(values, payloads, 32));
            uint[] sortedPayloads = new uint[Math.Max(1, values.Length)];
            for (int i = 0; i < values.Length; i++) sortedPayloads[i] = sorted[i * 2 + 1];
            fixture = fixture with { Input = RadixSortContract.Pack(values, payloads),
                ExpectedPayloads = WorkloadData.ToBytes(sortedPayloads) };
        }
        return UnifiedWorkloads.Build(assetRoot, fixture, implementation switch
        {
            SortImplementation.InternalBinary => "internal-radix-1",
            SortImplementation.AmdParallelSort => "amd-parallel-sort",
            _ => throw new ArgumentOutOfRangeException(nameof(implementation))
        });
    }

    /// <summary>Return an explicit, validated fallback on absent/mismatched confirmation or capability.
    /// An opt-in unmeasured request still cannot bypass source or capability checks.</summary>
    public static OperationSelection Select(string assetRoot, UnifiedOperationPlan requested,
        UnifiedOperationPlan fallback, OperationRuntime runtime, OperationDeploymentProfile? profile = null,
        bool allowUnmeasured = false, string? trustedConfirmationSha256 = null)
    {
        runtime.Validate(); requested.Validate(); fallback.Validate();
        if (requested.SemanticId != fallback.SemanticId || requested.LogicalCount != fallback.LogicalCount ||
            requested.InputSha256 != fallback.InputSha256 ||
            !requested.Outputs.Select(o => o.ExpectedSha256).SequenceEqual(fallback.Outputs.Select(o => o.ExpectedSha256)))
            throw new InvalidDataException("A fallback must preserve input, full output semantics and payloads.");
        if (fallback.Implementation is not ("internal-scan-baseline" or "internal-radix-1"))
            throw new InvalidDataException("Fallback must be an explicit established baseline.");
        if (!Compatible(fallback, runtime)) throw new InvalidDataException("No compatible baseline is available.");
        string fallbackHash = OperationIdentity.Compute(fallback, assetRoot);
        OperationSelection FallBack(string reason) => new(fallback, fallbackHash,
            OperationPerformanceStatus.Unmeasured, true, reason);
        if (requested.Implementation is not ("internal-scan-baseline" or "internal-radix-1" or
            "gps-reduce-then-scan" or "amd-parallel-sort" or "wave-tiled-exclusive"))
            return FallBack("Requested implementation has no reviewed full-width SDK contract.");
        if (!Compatible(requested, runtime)) return FallBack("Required shader model / wave capability unavailable.");
        string requestedHash;
        try { VerifyPinnedSource(assetRoot, requested.Implementation); requestedHash = OperationIdentity.Compute(requested, assetRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return FallBack("Requested shader assets are unavailable or invalid: " + error.Message); }
        if (profile is not null)
        {
            bool confirmed = profile.Implementation == requested.Implementation && profile.SemanticId == requested.SemanticId &&
                profile.Abi == UnifiedOperationPlan.AbiId && profile.SourceAndPlanSha256 == requestedHash &&
                profile.Runtime == runtime && profile.PerformanceStatus == OperationPerformanceStatus.Confirmed &&
                OperationIdentity.IsHash(trustedConfirmationSha256) && profile.ConfirmationSha256 == trustedConfirmationSha256;
            return confirmed ? new(requested, requestedHash, OperationPerformanceStatus.Confirmed, false, "Exact reviewed deployment identity matched.") :
                FallBack("Profile source, operation, ABI, runtime or trusted confirmation identity mismatch.");
        }
        return allowUnmeasured ? new(requested, requestedHash, OperationPerformanceStatus.Unmeasured, false, "Explicit opt-in; GPU correctness and performance remain unmeasured.") :
            FallBack("No exact confirmed profile; unmeasured alternatives require explicit opt-in.");
    }

    public static void VerifyPinnedSource(string assetRoot, string implementation)
    {
        string? sourceName = implementation switch { "gps-reduce-then-scan" => "gpu-prefix-sums", "amd-parallel-sort" => "amd-fidelityfx-parallelsort", _ => null };
        if (sourceName is null) return;
        string root = Path.GetFullPath(assetRoot);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "third_party/upstream-lock.json")));
        try
        {
            var source = json.RootElement.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("name").GetString() == sourceName);
            string expectedCommit = sourceName == "gpu-prefix-sums" ? "98d93a4e9ed2f3c8353119515bf9be90a2e137ad" : "c6efa6bf7f2027b3ec94f28578bb5965eabb9e55";
            if (source.GetProperty("commit").GetString() != expectedCommit)
                throw new InvalidDataException("External source revision differs from reviewed SDK adapter.");
            var files = source.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                throw new InvalidDataException("External source lock requires a nonempty file list.");
            foreach (var file in files.EnumerateArray())
            {
                string path = Path.GetFullPath(Path.Combine(root, file.GetProperty("localPath").GetString()!));
                string relative = Path.GetRelativePath(Path.Combine(root, "third_party", sourceName), path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                    throw new InvalidDataException("External source escapes its reviewed directory.");
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length != file.GetProperty("bytes").GetInt32() || ContentHash.Sha256(bytes) != file.GetProperty("sha256").GetString())
                    throw new InvalidDataException("External source bytes differ from source lock: " + relative);
            }
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Normalize malformed lock data here, without hiding unrelated plan errors
            // behind a broad catch in Select. Caller root validation remains outside.
            throw new InvalidDataException("External source lock has an invalid structure.", error);
        }
    }

    private static bool Compatible(UnifiedOperationPlan plan, OperationRuntime runtime)
    {
        if (runtime.Backend != "D3D12" || !Version.TryParse(runtime.ShaderModel.Replace('_', '.'), out var model)) return false;
        if (plan.Shaders.Any(s => !Version.TryParse(s.ShaderModel.Replace('_', '.'), out var required) || model < required)) return false;
        return plan.Shaders.All(s =>
        {
            int wave = s.Defines.TryGetValue("HLSLPERF_WAVE_SIZE", out var text) ? int.Parse(text) :
                s.Defines.ContainsKey("FFX_PREFER_WAVE64") ? 64 : 0;
            return wave == 0 || wave >= runtime.MinimumWaveSize && wave <= runtime.MaximumWaveSize;
        });
    }
}
