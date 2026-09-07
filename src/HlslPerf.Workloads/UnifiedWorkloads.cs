using System.Globalization;
using System.Text.Json.Serialization;
using HlslPerf.Core;

namespace HlslPerf.Workloads;

public sealed record UnifiedFixture(string Workload, int Count, int Seed, string Pattern, bool Pairs,
    [property: JsonIgnore] byte[] Input,
    [property: JsonIgnore] byte[] ExpectedKeys,
    [property: JsonIgnore] byte[]? ExpectedPayloads)
{
    public string InputSha256 => ContentHash.Sha256(Input);
    public string ExpectedKeysSha256 => ContentHash.Sha256(ExpectedKeys);
    public string? ExpectedPayloadsSha256 => ExpectedPayloads is null ? null : ContentHash.Sha256(ExpectedPayloads);
}

/// <summary>Host-only binding/scheduling adapters; upstream shader files remain unmodified.</summary>
public static class UnifiedWorkloads
{
    public static readonly string[] ScanImplementations = ["internal-scan-baseline", "internal-scan-single", "gps-reduce-then-scan", "gps-decoupled-fallback"];
    public static readonly string[] RadixImplementations = ["internal-radix-1", "internal-radix-8", "amd-parallel-sort"];

    public static UnifiedFixture Fixture(string workload, int count, int seed, string pattern = "uniform", bool pairs = false, uint[]? explicitInput = null)
    {
        if (count < 0 || (workload != "scan" && workload != "radix") || (workload == "scan" && pairs))
            throw new ArgumentOutOfRangeException(nameof(count));
        uint[] values = explicitInput ?? WorkloadData.AsUInt32(WorkloadData.GenerateUInt32(count, seed));
        if (values.Length != count) throw new InvalidDataException("Fixture input count mismatch.");
        if (explicitInput is null)
            for (int i = 0; i < count; i++) values[i] = pattern switch
            {
                "uniform" => values[i], "duplicate" => values[i] % 7, "equal" => 0x80000001,
                "descending" => (uint)(count - i), "ones" => 1, "zeros" => 0,
                "extremes" => (i % 4) switch { 0 => 0, 1 => uint.MaxValue, 2 => 0x80000000, _ => 1 },
                _ => throw new InvalidDataException("Unknown fixed input pattern.")
            };
        if (workload == "scan")
        {
            uint[] expected = new uint[Math.Max(1, count)]; uint sum = 0;
            for (int i = 0; i < count; i++) { expected[i] = sum; sum = unchecked(sum + values[i]); }
            return new(workload, count, seed, pattern, false,
                count == 0 ? new byte[4] : WorkloadData.ToBytes(values), WorkloadData.ToBytes(expected), null);
        }
        uint[]? payloads = pairs ? Enumerable.Range(0, count).Select(i => (uint)i).ToArray() : null;
        byte[] sorted = RadixSortContract.StableOracle(values, payloads, 32);
        uint[] sortedValues = WorkloadData.AsUInt32(sorted);
        uint[] keys = new uint[Math.Max(1, count)]; uint[]? sortedPayloads = pairs ? new uint[keys.Length] : null;
        for (int i = 0; i < count; i++)
        {
            keys[i] = sortedValues[i * (pairs ? 2 : 1)];
            if (pairs) sortedPayloads![i] = sortedValues[i * 2 + 1];
        }
        return new(workload, count, seed, pattern, pairs, RadixSortContract.Pack(values, payloads),
            WorkloadData.ToBytes(keys), pairs ? WorkloadData.ToBytes(sortedPayloads!) : null);
    }

    public static UnifiedOperationPlan Build(string repository, UnifiedFixture fixture, string implementation)
    {
        string repo = Path.GetFullPath(repository);
        if (!(fixture.Workload == "scan" ? ScanImplementations : RadixImplementations).Contains(implementation))
            throw new InvalidDataException("Implementation does not support this workload.");
        UnifiedOperationPlan plan = fixture.Count == 0 ? Empty(fixture, implementation) : implementation switch
        {
            "gps-reduce-then-scan" => Prefix(repo, fixture, false),
            "gps-decoupled-fallback" => Prefix(repo, fixture, true),
            "amd-parallel-sort" => ParallelSort(repo, fixture),
            _ => Internal(repo, fixture, implementation)
        };
        plan.Validate(); return plan;
    }

    private static string Semantic(UnifiedFixture f) => f.Workload == "scan" ? "exclusive-u32-sum-modulo-2^32" : f.Pairs ? "stable-full32-u32-sort-aos-input-soa-output" : "ascending-full32-u32-sort";
    private static UnifiedPass Copy(string name, UnifiedStage stage, string source, string destination, int bytes) =>
        new(name, stage, null, null, [], [], []) { CopySource = source, CopyDestination = destination, CopyBytes = bytes };

    private static UnifiedOperationPlan Empty(UnifiedFixture f, string implementation)
    {
        List<KernelBufferSpec> buffers = [new("empty-input", 4, new byte[4]), new("keys", 4)];
        List<UnifiedPass> passes = [Copy("empty-guard", UnifiedStage.InputRestore, "empty-input", "keys", 4)];
        List<KernelVerifiedOutput> outputs = [new("keys", f.ExpectedKeysSha256)];
        if (f.Pairs) { buffers.Add(new("payloads", 4)); passes.Add(Copy("empty-payload", UnifiedStage.InputRestore, "empty-input", "payloads", 4)); outputs.Add(new("payloads", f.ExpectedPayloadsSha256!)); }
        return new(implementation, 0, Semantic(f), f.InputSha256, buffers, [], passes, outputs) { ImmutableInputs = ["empty-input"] };
    }

    private static UnifiedShader Shader(string id, string path, string entry, IReadOnlyDictionary<string, int> defines, string[] includes) =>
        new(id, path, entry, defines.ToDictionary(p => p.Key, p => p.Value.ToString(CultureInfo.InvariantCulture)), includes, []);

    private static UnifiedOperationPlan Internal(string repo, UnifiedFixture f, string implementation)
    {
        bool scan = f.Workload == "scan", single = implementation == "internal-scan-single", wide = implementation == "internal-radix-8";
        Dictionary<string, int> defines = new()
        {
            ["HLSLPERF_GROUP_SIZE"] = scan || (!wide && !f.Pairs) ? 256 : 128,
            ["HLSLPERF_ELEMENTS_PER_THREAD"] = wide ? 2 : 4,
            ["HLSLPERF_SCAN_BACKEND"] = scan ? single ? 3 : 1 : 2,
            ["HLSLPERF_SCAN_OPERATOR"] = 1, ["HLSLPERF_VECTOR_WIDTH"] = 1
        };
        if (single) { defines["HLSLPERF_SINGLE_PASS_GROUPS"] = 256; defines["HLSLPERF_SINGLE_PASS_ITEMS_SCALE"] = 4; defines["HLSLPERF_WAVE_SIZE"] = 32; }
        if (!scan) { defines["HLSLPERF_RADIX_BITS"] = wide ? 8 : 1; defines["HLSLPERF_RADIX_PAIRS"] = f.Pairs ? 1 : 0; defines["HLSLPERF_WAVE_SIZE"] = !wide && f.Pairs ? 64 : 32; }
        string workload = scan ? "exclusive-scan-u32-v1" : f.Pairs ? "radix-sort-pairs-u32-v1" : "radix-sort-u32-v1";
        string path = Path.Combine(repo, "kernels", scan ? "scan.hlsl" : "radix-sort.hlsl");
        TuningManifest manifest = new() { Name = implementation, KernelPath = path, SchemaVersion = "3.0", ShaderModel = "6_6", KernelAbiVersion = KernelAbiV2.Id,
            WorkItemCount = f.Count, Axes = [], Workload = new() { Id = workload, Parameters = new Dictionary<string, long> { ["elementCount"] = f.Count, ["seed"] = f.Seed, ["bitCount"] = 32, ["keyPattern"] = 1, ["keyDomain"] = 1 } } };
        KernelExecutionPlan legacy = new BuiltinWorkloadProvider().Create(workload).Build(manifest, new KernelCandidate(defines));
        KernelBufferSpec[] buffers = legacy.Buffers.Select(buffer => buffer.Name == "input" ? buffer with { InitialData = f.Input } : buffer).ToArray();
        List<UnifiedShader> shaders = [];
        string shaderPrefix = implementation + "/" + (f.Pairs ? "pairs" : "keys") + "/";
        foreach (string entry in legacy.Passes.Select(pass => pass.EntryPoint).Distinct()) shaders.Add(Shader(shaderPrefix + entry, path, entry, defines, [Path.Combine(repo, "kernels")]));
        UnifiedPass[] passes = legacy.Passes.Select(pass => new UnifiedPass(pass.Name,
            pass.Name == "single-pass-reset" ? UnifiedStage.ScratchInitialization : pass.Name == "split-radix-pairs" ? UnifiedStage.OutputConversion : UnifiedStage.Algorithm,
            shaderPrefix + pass.EntryPoint, pass.Dispatch, [pass.Input0, pass.Input1], [pass.Output0, pass.Output1], pass.Constants)).ToArray();
        List<KernelVerifiedOutput> outputs = [new(legacy.VerifiedResource, f.ExpectedKeysSha256)];
        if (f.Pairs) outputs.Add(new("sorted-payloads", f.ExpectedPayloadsSha256!));
        return new(implementation, f.Count, Semantic(f), f.InputSha256, buffers, shaders, passes, outputs) { ImmutableInputs = ["input"] };
    }

    private static UnifiedOperationPlan Prefix(string repo, UnifiedFixture f, bool fallback)
    {
        string implementation = fallback ? "gps-decoupled-fallback" : "gps-reduce-then-scan";
        string folder = Path.Combine(repo, "third_party/gpu-prefix-sums/GPUPrefixSumsD3D12/Shaders");
        string path = Path.Combine(folder, fallback ? "ChainedScanDecoupledLookbackDecoupledFallback.hlsl" : "ReduceThenScan.hlsl");
        int aligned = checked((f.Count + 3) / 4 * 4), partitions = (f.Count + 3071) / 3072;
        byte[] input = new byte[aligned * 4]; f.Input.CopyTo(input, 0);
        List<KernelBufferSpec> buffers = [new("input", input.Length, input), new("scan", input.Length), new("reductions", partitions * 4)];
        List<UnifiedShader> shaders = []; List<UnifiedPass> passes = [];
        uint[] constants = [(uint)(aligned / 4), (uint)partitions, fallback ? 0u : 1u, 0];
        void Add(string entry, UnifiedStage stage, uint dispatch, string?[] uavs, uint[]? supplied = null)
        {
            string id = implementation + "/" + entry;
            // The upstream DXC 1.8 host supplies no -HV override (default 2021),
            // and queries support capped at SM 6.7. Vortice defaults to HLSL 2018.
            shaders.Add(Shader(id, path, entry, new Dictionary<string, int>(), [folder]) with
                { HlslVersion = 2021, ShaderModel = "6_7", EnableStrictness = false });
            passes.Add(new(entry, stage, id, new(dispatch), [], uavs, supplied ?? constants));
        }
        if (fallback)
        {
            buffers.Add(new("bump", 4));
            Add("InitCSDLDF", UnifiedStage.ScratchInitialization, 256, [null, null, "bump", "reductions"], [0, (uint)partitions, 0, 0]);
            Add("ChainedScanDecoupledLookbackDecoupledFallbackExclusive", UnifiedStage.Algorithm, (uint)partitions, ["input", "scan", "bump", "reductions"]);
        }
        else
        {
            Add("Reduce", UnifiedStage.Algorithm, (uint)partitions, ["input", null, "reductions"]);
            Add("Scan", UnifiedStage.Algorithm, 1, [null, null, "reductions"], [0, (uint)partitions, 0, 0]);
            Add("PropagateExclusive", UnifiedStage.Algorithm, (uint)partitions, ["input", "scan", "reductions"]);
        }
        string output = "scan";
        if (aligned != f.Count) { output = "canonical-scan"; buffers.Add(new(output, f.Count * 4)); passes.Add(Copy("trim-vector-padding", UnifiedStage.OutputConversion, "scan", output, f.Count * 4)); }
        return new(implementation, f.Count, Semantic(f), f.InputSha256, buffers, shaders, passes, [new(output, f.ExpectedKeysSha256)]) { ImmutableInputs = ["input"] };
    }

    private static UnifiedOperationPlan ParallelSort(string repo, UnifiedFixture f)
    {
        string gpu = Path.Combine(repo, "third_party/amd-fidelityfx-parallelsort/sdk/include/FidelityFX/gpu");
        string folder = Path.Combine(repo, "third_party/amd-fidelityfx-parallelsort/sdk/src/backends/dx12/shaders/parallelsort");
        uint blocks = (uint)((f.Count + 511) / 512), groups = Math.Min(800u, blocks);
        uint blocksPerGroup = blocks < 800 ? 1u : blocks / 800, extra = blocks < 800 ? 0 : blocks % 800;
        uint reduceGroups = 16 * ((512 > groups) ? 1 : (groups + 511) / 512);
        List<KernelBufferSpec> buffers = [new("input", f.Input.Length, f.Input), new("keys-a", f.Count * 4), new("keys-b", f.Count * 4),
            new("sums", checked((int)(16 * blocks * 4))), new("reduced", checked((int)(16 * ((blocks + 511) / 512) * 4)))];
        List<UnifiedShader> shaders = []; List<UnifiedPass> passes = [];
        if (f.Pairs)
        {
            buffers.Add(new("payload-a", f.Count * 4)); buffers.Add(new("payload-b", f.Count * 4));
            Dictionary<string, int> splitDefines = new() { ["HLSLPERF_GROUP_SIZE"] = 128, ["HLSLPERF_ELEMENTS_PER_THREAD"] = 4,
                ["HLSLPERF_SCAN_BACKEND"] = 2, ["HLSLPERF_SCAN_OPERATOR"] = 1, ["HLSLPERF_VECTOR_WIDTH"] = 1, ["HLSLPERF_RADIX_BITS"] = 4, ["HLSLPERF_RADIX_PAIRS"] = 1 };
            shaders.Add(Shader("amd/input-soa-adapter", Path.Combine(repo, "kernels/radix-sort.hlsl"), "SplitRadixPairs", splitDefines, [Path.Combine(repo, "kernels")]));
            uint splitGroups = (uint)((f.Count + 511) / 512);
            passes.Add(new("restore-and-deinterleave-input", UnifiedStage.InputRestore, "amd/input-soa-adapter", new(splitGroups), ["input"], ["keys-a", "payload-a"], [(uint)f.Count, 512, 0, 0, 0, 0, splitGroups, splitGroups]));
        }
        else passes.Add(Copy("restore-unsorted-input", UnifiedStage.InputRestore, "input", "keys-a", f.Count * 4));
        foreach (string pass in new[] { "sum", "reduce", "scan", "scan_add", "scatter" })
            shaders.Add(new("amd/" + pass + "/" + f.Pairs, Path.Combine(folder, "ffx_parallelsort_" + pass + "_pass.hlsl"), "CS",
                new Dictionary<string, string> { ["FFX_GPU"] = "1", ["FFX_HLSL"] = "1", ["FFX_HALF"] = "0", ["FFX_HLSL_SM"] = "66",
                    ["FFX_PARALLELSORT_OPTION_HAS_PAYLOAD"] = f.Pairs ? "1" : "0", ["FFX_PREFER_WAVE64"] = "[WaveSize(64)]" },
                [gpu, Path.Combine(gpu, "parallelsort"), folder], ["-Wno-for-redefinition", "-Wno-ambig-lit-shift"])
                { HlslVersion = 2021, EnableStrictness = false });
        for (uint digit = 0; digit < 8; digit++)
        {
            string source = digit % 2 == 0 ? "keys-a" : "keys-b", destination = digit % 2 == 0 ? "keys-b" : "keys-a";
            uint[] constants = [(uint)f.Count, blocksPerGroup, groups, extra, reduceGroups / 16, reduceGroups, digit * 4, 0];
            void Add(string kind, uint dispatch, string?[] bindings) => passes.Add(new($"digit-{digit}-{kind}", UnifiedStage.Algorithm, "amd/" + kind + "/" + f.Pairs, new(dispatch), [], bindings, constants));
            Add("sum", groups, [source, "sums"]); Add("reduce", reduceGroups, ["sums", "reduced"]);
            Add("scan", 1, ["reduced", "reduced"]); Add("scan_add", reduceGroups, ["sums", "sums", "reduced"]);
            Add("scatter", groups, f.Pairs ? [source, destination, "sums", digit % 2 == 0 ? "payload-a" : "payload-b", digit % 2 == 0 ? "payload-b" : "payload-a"] : [source, destination, "sums"]);
        }
        List<KernelVerifiedOutput> outputs = [new("keys-a", f.ExpectedKeysSha256)];
        if (f.Pairs) outputs.Add(new("payload-a", f.ExpectedPayloadsSha256!));
        return new("amd-parallel-sort", f.Count, Semantic(f), f.InputSha256, buffers, shaders, passes, outputs) { ImmutableInputs = ["input"] };
    }
}
