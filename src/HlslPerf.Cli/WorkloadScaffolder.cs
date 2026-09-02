using System.Text;
using System.Text.RegularExpressions;
using HlslPerf.Core;

namespace HlslPerf.Cli;

internal static partial class WorkloadScaffolder
{
    public static IReadOnlyList<string> Create(string targetDirectory, string workloadId, string className)
    {
        if (!WorkloadIdRegex().IsMatch(workloadId))
            throw new ArgumentException(
                "Workload id must start with a lowercase letter or digit and contain only lowercase letters, digits, '.', '-' or '_'.");
        if (!ClassNameRegex().IsMatch(className))
            throw new ArgumentException("Class name must be a valid C# identifier starting with an uppercase letter.");

        string root = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException($"Target directory is not empty: {root}");
        Directory.CreateDirectory(root);

        string projectName = className + ".Workload";
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [$"{projectName}.csproj"] = Project(projectName),
            [$"{className}Provider.cs"] = Provider(className, workloadId),
            ["kernel.hlsl"] = Kernel(),
            ["manifest.json"] = Manifest(workloadId),
            ["README.md"] = Readme(projectName, workloadId),
            [".gitignore"] = "bin/\nobj/\n.hlslperf/\n"
        };
        UTF8Encoding utf8 = new(false);
        foreach ((string relativePath, string content) in files)
            File.WriteAllText(Path.Combine(root, relativePath), content.Replace("\r\n", "\n"), utf8);
        return files.Keys.Select(path => Path.Combine(root, path)).ToArray();
    }

    private static string Project(string projectName) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <AssemblyName>{{projectName}}</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="EdwinLiu.HlslPerf.Core" Version="{{HlslPerfSdk.Version}}" />
          </ItemGroup>
        </Project>
        """;

    private static string Provider(string className, string workloadId) => $$"""
        using System.Runtime.InteropServices;
        using HlslPerf.Core;

        namespace {{className}}Plugin;

        public sealed class {{className}}Provider : IKernelWorkloadProvider
        {
            public IReadOnlyCollection<string> WorkloadIds { get; } = ["{{workloadId}}"];

            public IKernelWorkload Create(string workloadId) => workloadId == "{{workloadId}}"
                ? new {{className}}Workload()
                : throw new InvalidDataException($"Unknown workload '{workloadId}'.");
        }

        internal sealed class {{className}}Workload : IKernelWorkload
        {
            public string Id => "{{workloadId}}";

            public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate)
            {
                WorkloadSpec spec = manifest.Workload
                    ?? throw new InvalidDataException("A workload object is required.");
                int elementCount = spec.GetRequiredInt32("elementCount");
                int seed = spec.GetInt32("seed", 1);
                int groupSize = candidate.GetRequired("HLSLPERF_GROUP_SIZE");
                int elementsPerThread = candidate.GetRequired("HLSLPERF_ELEMENTS_PER_THREAD");
                uint[] input = new uint[elementCount];
                uint state = unchecked((uint)seed);
                for (int index = 0; index < input.Length; ++index)
                {
                    state = unchecked(state * 1_664_525u + 1_013_904_223u);
                    input[index] = state ^ unchecked((uint)index * 2_246_822_519u);
                }
                byte[] bytes = MemoryMarshal.AsBytes(input.AsSpan()).ToArray();
                long blockSize = checked((long)groupSize * elementsPerThread);
                uint groups = checked((uint)((elementCount + blockSize - 1) / blockSize));
                uint dispatchX = Math.Min(65_535u, groups);
                uint dispatchY = (groups + dispatchX - 1) / dispatchX;
                KernelExecutionPlan plan = new(
                    Id,
                    KernelAbiV1.Id,
                    elementCount,
                    [
                        new KernelBufferSpec("input", bytes.Length, bytes),
                        new KernelBufferSpec("output", bytes.Length)
                    ],
                    [
                        new KernelPassSpec(
                            "copy",
                            "CopyKernel",
                            new KernelDispatch(dispatchX, dispatchY),
                            "input",
                            null,
                            "output",
                            null,
                            [(uint)elementCount, 0, 0, 0, 0, 0, dispatchX, groups])
                    ],
                    "output",
                    ContentHash.Sha256(bytes));
                plan.Validate();
                return plan;
            }
        }
        """;

    private static string Kernel() => """
        #define HLSLPERF_ROOT_SIGNATURE "SRV(t0), SRV(t1), UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

        ByteAddressBuffer Input0 : register(t0);
        RWByteAddressBuffer Output0 : register(u0);

        cbuffer DispatchParameters : register(b0)
        {
            uint ElementCount;
            uint Parameter1;
            uint Parameter2;
            uint Parameter3;
            uint Parameter4;
            uint Parameter5;
            uint DispatchGroupsX;
            uint DispatchGroupCount;
        };

        [RootSignature(HLSLPERF_ROOT_SIGNATURE)]
        [numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
        void CopyKernel(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
        {
            const uint linearGroup = groupId.y * DispatchGroupsX + groupId.x;
            if (linearGroup >= DispatchGroupCount)
                return;
            const uint blockStart = linearGroup * HLSLPERF_GROUP_SIZE * HLSLPERF_ELEMENTS_PER_THREAD;
            const uint threadStart = blockStart + groupIndex * HLSLPERF_ELEMENTS_PER_THREAD;
            [unroll]
            for (uint item = 0; item < HLSLPERF_ELEMENTS_PER_THREAD; ++item)
            {
                const uint index = threadStart + item;
                if (index < ElementCount)
                    Output0.Store(index * 4, Input0.Load(index * 4));
            }
        }
        """;

    private static string Manifest(string workloadId) => $$"""
        {
          "schemaVersion": "3.0",
          "name": "{{workloadId}}",
          "kernelPath": "kernel.hlsl",
          "kernelAbiVersion": "hlslperf.raw-buffer.v1",
          "shaderModel": "6_0",
          "workItemCount": 16777216,
          "warmupDispatches": 4,
          "minimumWarmupMilliseconds": 75,
          "measurementBatches": 15,
          "dispatchesPerBatch": 4,
          "minimumBatchMilliseconds": 10,
          "maximumDispatchesPerBatch": 256,
          "maximumCoefficientOfVariation": 0.05,
          "minimumRequiredSpeedup": 1.01,
          "workload": {
            "id": "{{workloadId}}",
            "parameters": { "elementCount": 16777216, "seed": 19088743 }
          },
          "baselineDefines": {
            "HLSLPERF_GROUP_SIZE": 256,
            "HLSLPERF_ELEMENTS_PER_THREAD": 1
          },
          "axes": [
            { "name": "HLSLPERF_GROUP_SIZE", "values": [64, 128, 256, 512] },
            { "name": "HLSLPERF_ELEMENTS_PER_THREAD", "values": [1, 2, 4, 8] }
          ]
        }
        """;

    private static string Readme(string projectName, string workloadId) => $$"""
        # {{workloadId}}

        This is an out-of-tree HlslPerf workload plugin generated by `hlslperf new-workload`.
        Loading a plugin executes trusted .NET code; never load an untrusted assembly.

        Build after restoring `EdwinLiu.HlslPerf.Core` {{HlslPerfSdk.Version}} from your configured package source:

            dotnet build -c Release

        Discover and tune it:

            hlslperf workloads --plugin bin/Release/net10.0/{{projectName}}.dll
            hlslperf tune manifest.json --plugin bin/Release/net10.0/{{projectName}}.dll --rga off
        """;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkloadIdRegex();

    [GeneratedRegex("^[A-Z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassNameRegex();
}
