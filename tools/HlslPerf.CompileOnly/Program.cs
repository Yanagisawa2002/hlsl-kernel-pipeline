using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.Workloads;
using Vortice.Dxc;

// Deliberately no D3D12 reference, device, timer, query heap or executor.
if (args.Length != 2) throw new ArgumentException("Usage: HlslPerf.CompileOnly <asset-root> <new-output-directory>");
string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new InvalidDataException("Choose a new compile output directory.");
Directory.CreateDirectory(output);
List<UnifiedOperationPlan> plans =
[
    PrimitiveOperations.ExclusiveScan(root, [uint.MaxValue, 1, 2]),
    PrimitiveOperations.ExclusiveScan(root, [uint.MaxValue, 1, 2], ScanImplementation.GpuPrefixSumsReduceThenScan),
    PrimitiveOperations.StableSort(root, [uint.MaxValue, 0, 0]),
    PrimitiveOperations.StableSort(root, [uint.MaxValue, 0, 0], [3, 1, uint.MaxValue]),
    PrimitiveOperations.StableSort(root, [uint.MaxValue, 0, 0], implementation: SortImplementation.AmdParallelSort),
    PrimitiveOperations.StableSort(root, [uint.MaxValue, 0, 0], [3, 1, uint.MaxValue], SortImplementation.AmdParallelSort)
];
List<object> compiledShaders = [];
foreach (var shader in plans.SelectMany(p => p.Shaders).DistinctBy(s => s.Id))
{
    var options = new DxcCompilerOptions
    {
        ShaderModel = shader.ShaderModel == "6_7" ? DxcShaderModel.Model6_7 : DxcShaderModel.Model6_6,
        HLSLVersion = shader.HlslVersion, OptimizationLevel = 3, EnableStrictness = shader.EnableStrictness,
        WarningsAreErrors = false // Upstream warnings are retained verbatim.
    };
    string[] arguments = shader.IncludeDirectories.SelectMany(path => new[] { "-I", path })
        .Concat(shader.CompilerArguments).Append("-Qstrip_rootsignature").ToArray();
    using var result = DxcCompiler.Compile(DxcShaderStage.Compute, File.ReadAllText(shader.SourcePath), shader.EntryPoint,
        options, shader.SourcePath, shader.Defines.Select(p => new DxcDefine { Name = p.Key, Value = p.Value }).ToArray(), null, arguments);
    string diagnostics = result.GetErrors();
    if (result.GetStatus().Failure) throw new InvalidDataException(shader.Id + ": " + diagnostics);
    byte[] dxil = result.GetObjectBytecodeArray();
    string file = ContentHash.Sha256(shader.Id) + ".dxil";
    File.WriteAllBytes(Path.Combine(output, file), dxil);
    compiledShaders.Add(new { shader, file, dxilSha256 = ContentHash.Sha256(dxil), diagnostics });
}
File.WriteAllText(Path.Combine(output, "compile-only.json"), JsonSerializer.Serialize(new
{
    schema = "hlslperf.compile-only.v1", performanceStatus = "Unmeasured", gpuExecuted = false,
    plans = plans.Select(p => new { p.Implementation, p.SemanticId, identity = OperationIdentity.Compute(p, root) }),
    compilerFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) is "dxcompiler.dll" or "dxil.dll")
        .Select(path => new { path, sha256 = ContentHash.Sha256(File.ReadAllBytes(path)) }),
    compiledShaders
}, JsonDefaults.Options));
Console.WriteLine($"Compiled {compiledShaders.Count} shader entries. GPU/benchmark execution: none. Performance: Unmeasured.");
