using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;
using Vortice.Dxc;

internal static class CausalScanRunner
{
    public static int Run(string repo, string output, int processIndex)
    {
        // Index zero is correctness and actual-DXIL inspection only, never timed.
        if (processIndex is < 0 or > 3 || Directory.Exists(output)) throw new InvalidDataException("Invalid causal diagnostic slot or existing output.");
        Directory.CreateDirectory(output);
        using var tuner = new D3D12Tuner("R9700"); using var executor = tuner.CreateUnifiedExecutor();
        var runtime = UnifiedBenchRunner.RuntimeIdentity(); var started = DateTimeOffset.UtcNow;
        List<object> results = [], errors = [], shaderEvidence = []; bool completed = false;
        try
        {
            if (processIndex == 0)
            {
                foreach (int count in new[] { 0, 1, 3, 4, 15, 16, 17, 255, 256, 257, 4095, 4096, 4097, 8191, 8192, 8193, 8388608 })
                    foreach (string pattern in new[] { "uniform", "zeros", "ones", "extremes" })
                    {
                        var fixture = UnifiedWorkloads.Fixture("scan", count, 1909087, pattern);
                        using var session = executor.Prepare(CausalScanWorkloads.Build(repo, fixture, CausalScanWorkloads.VectorIo));
                        var verification = session.Verify(); results.Add(new { phase = "correctness", fixture, arm = CausalScanWorkloads.VectorIo, verification });
                        if (verification.Any(v => !v.Passed)) throw new InvalidDataException("Candidate correctness failed.");
                        Save();
                    }
                foreach (string arm in CausalScanWorkloads.Arms)
                {
                    var plan = CausalScanWorkloads.Build(repo, UnifiedWorkloads.Fixture("scan", 8388608, 1909087), arm);
                    using var session = executor.Prepare(plan);
                    var verification = session.Verify(); results.Add(new { phase = "mechanism-correctness", arm, verification });
                    if (verification.Any(v => !v.Passed)) throw new InvalidDataException("Mechanism correctness failed.");
                    foreach (var shader in plan.Shaders) shaderEvidence.Add(ExportShader(shader, output));
                }
            }
            else
            {
                var fixture = UnifiedWorkloads.Fixture("scan", 8388608, 1909087 + processIndex * 7907);
                var sessions = new Dictionary<string, D3D12Tuner.UnifiedSession>();
                string[] arms = CausalScanWorkloads.Arms;
                string[][] permutations = [[arms[0],arms[1],arms[2]], [arms[0],arms[2],arms[1]], [arms[1],arms[0],arms[2]],
                    [arms[1],arms[2],arms[0]], [arms[2],arms[0],arms[1]], [arms[2],arms[1],arms[0]]];
                try
                {
                    foreach (string arm in arms)
                    {
                        var session = executor.Prepare(CausalScanWorkloads.Build(repo, fixture, arm)); sessions.Add(arm, session);
                        var verification = session.Verify(); results.Add(new { phase="before", fixture, arm, verification });
                        if (verification.Any(v=>!v.Passed)) throw new InvalidDataException("Diagnostic correctness failed.");
                    }
                    for (int w=0;w<8;w++) foreach (string arm in permutations[(w+processIndex)%6])
                        results.Add(new { phase="warmup", arm, timing=sessions[arm].MeasureBatch(6) });
                    for (int block=0;block<6;block++)
                    {
                        var order=permutations[(block+processIndex)%6];
                        foreach (string arm in order.Concat(order.Reverse()))
                            results.Add(new { phase="whole-timing",fixture,arm,block,timing=sessions[arm].MeasureBatch(18) });
                        Save();
                    }
                    foreach (string arm in arms)
                    {
                        var verification=sessions[arm].Verify();results.Add(new { phase="after",arm,verification });
                        if (verification.Any(v=>!v.Passed)) throw new InvalidDataException("Post correctness failed.");
                    }
                }
                finally { foreach (var session in sessions.Values) session.Dispose(); }
            }
            completed=true;Save();return 0;
        }
        catch (Exception error) { errors.Add(error.ToString());Save();return 3; }
        void Save() => File.WriteAllText(Path.Combine(output,"diagnostic.json"),JsonSerializer.Serialize(new {
            schema="hlslperf.causal-scan-diagnostic.v1",developmentOnly=true,completed,pid=Environment.ProcessId,processIndex,
            startedUtc=started,recordedUtc=DateTimeOffset.UtcNow,device=tuner.DescribeDevice("per-arm:6_6-or-6_7"),
            deviceRemovalStatus=tuner.DeviceRemovalStatus,runtime,compilation=executor.CompilationEvidence,shaderEvidence,results,errors },JsonDefaults.Options));
    }

    private static object ExportShader(UnifiedShader shader, string output)
    {
        var options=new DxcCompilerOptions { ShaderModel=shader.ShaderModel=="6_7" ? DxcShaderModel.Model6_7 : DxcShaderModel.Model6_6,
            HLSLVersion=shader.HlslVersion,OptimizationLevel=3,EnableStrictness=shader.EnableStrictness,WarningsAreErrors=false };
        string[] args=shader.IncludeDirectories.SelectMany(d=>new[]{"-I",d}).Concat(shader.CompilerArguments).Append("-Qstrip_rootsignature").ToArray();
        using var result=DxcCompiler.Compile(DxcShaderStage.Compute,File.ReadAllText(shader.SourcePath),shader.EntryPoint,options,shader.SourcePath,
            shader.Defines.Select(p=>new DxcDefine { Name=p.Key,Value=p.Value }).ToArray(),null,args);
        if(result.GetStatus().Failure) throw new InvalidDataException(result.GetErrors());
        byte[] bytes=result.GetObjectBytecodeArray(); using var blob=result.GetOutput(DxcOutKind.Object);
        var buffer=new DxcBuffer { Ptr=blob.BufferPointer,Size=(nuint)bytes.Length };
        using var compiler=Dxc.CreateDxcCompiler<IDxcCompiler3>();
        using var disassembled=compiler.Disassemble<IDxcResult>(in buffer);
        if(disassembled.GetStatus().Failure) throw new InvalidDataException(disassembled.GetErrors());
        using var text=disassembled.GetOutput<IDxcBlobUtf8>(DxcOutKind.Disassembly);
        string prefix=Path.Combine(output,shader.Id.Replace('/','_'));
        File.WriteAllBytes(prefix+".dxil",bytes);File.WriteAllText(prefix+".ll",text.StringPointer);
        return new {shaderId=shader.Id,dxilSha256=ContentHash.Sha256(bytes),disassemblySha256=ContentHash.Sha256(File.ReadAllBytes(prefix+".ll")),
            dxilPath=prefix+".dxil",disassemblyPath=prefix+".ll",arguments=args,
            limitation="Compiler intermediate representation, not device ISA, occupancy, or DRAM/cache counters. Compare hash with executor compilation evidence."};
    }
}
