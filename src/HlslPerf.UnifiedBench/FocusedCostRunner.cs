using System.Runtime.InteropServices;
using System.Text.Json;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.Workloads;

internal static class FocusedCostRunner
{
    public static int Run(string repository, string outputDirectory, int processIndex)
    {
        if (processIndex is < 1 or > 3) throw new InvalidDataException("The diagnostic has exactly three process slots.");
        string repo=Path.GetFullPath(repository), output=Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new InvalidDataException("Never overwrite diagnostic evidence.");
        Directory.CreateDirectory(output);
        using var tuner=new D3D12Tuner("R9700"); using var executor=tuner.CreateUnifiedExecutor();
        var runtime=UnifiedBenchRunner.RuntimeIdentity(); var started=DateTimeOffset.UtcNow;
        var results=new List<object>(); var errors=new List<string>(); bool completed=false;
        try
        {
            foreach (string workload in new[] { "radix", "scan" })
            {
                var fixture=UnifiedWorkloads.Fixture(workload, workload=="radix" ? 1048576 : 8388608,
                    20260908 + processIndex*977 + (workload=="scan" ? 31 : 0), "uniform", workload=="radix");
                string[] arms=workload=="radix" ? ["internal-radix-8", "amd-parallel-sort"] : ["internal-scan-single", "gps-reduce-then-scan"];
                var sessions=new Dictionary<string,D3D12Tuner.UnifiedSession>();
                try
                {
                    foreach (string arm in arms)
                    {
                        var plan=FocusedCostWorkloads.Build(repo,fixture,arm); var session=executor.Prepare(plan);sessions.Add(arm,session);
                        var verification=session.Verify();
                        results.Add(new { phase="before",fixture,arm,verification,plan.Passes,
                            buffers=plan.Buffers.Select(b=>new {b.Name,b.ByteLength}),session.GpuUploadMilliseconds,session.CpuPreparationMilliseconds });
                        if (verification.Any(v=>!v.Passed)) throw new InvalidDataException("Diagnostic correctness failed: "+arm);
                        for(int warmup=0;warmup<4;warmup++) session.MeasureBatch(6);
                    }
                    for(int block=0;block<6;block++)
                    {
                        string[] order=(block+processIndex)%2==0 ? arms : arms.Reverse().ToArray();
                        foreach (string arm in order.Concat(order.Reverse()))
                            results.Add(new {phase="pass-timing",fixture,arm,block,timing=sessions[arm].MeasurePassDiagnostic()});
                        Save();
                    }
                    foreach(string arm in arms)
                    {
                        var verification=sessions[arm].Verify(); results.Add(new {phase="after",fixture,arm,verification});
                        if(verification.Any(v=>!v.Passed)) throw new InvalidDataException("Diagnostic post-check failed: "+arm);
                    }
                }
                finally { foreach(var session in sessions.Values)session.Dispose(); }
                if(workload=="scan")
                {
                    using var counted=executor.Prepare(FocusedCostWorkloads.Build(repo,fixture,FocusedCostWorkloads.ScanCounters));
                    var before=counted.Verify(); if(before.Any(v=>!v.Passed))throw new InvalidDataException("Counter variant correctness failed.");
                    results.Add(new {phase="counter-before",fixture,verification=before});
                    for(int sample=0;sample<3;sample++)
                    {
                        var timing=counted.MeasureBatch(1);
                        uint[] counters=MemoryMarshal.Cast<byte,uint>(counted.ReadDiagnosticBuffer("diagnostic-counters")).ToArray();
                        for(int block=0;block<counters.Length/4;block++)
                            if(counters[block*4] != counters[block*4+1]+counters[block*4+2]+counters[block*4+3] || counters[block*4+2]>1)
                                throw new InvalidDataException("Lookback counter accounting failed.");
                        results.Add(new {phase="instrumented-lookback",fixture,sample,timing,counters,
                            interpretation="Four uints/block: status polls, aggregate hits, prefix hits, not-ready polls. Instrumentation changes timing and is not a DRAM/cache counter."});
                    }
                    var after=counted.Verify(); results.Add(new {phase="counter-after",fixture,verification=after});
                    if(after.Any(v=>!v.Passed))throw new InvalidDataException("Counter variant post-check failed.");
                }
                Save();Console.WriteLine($"Focused diagnostic p{processIndex}: {workload} completed.");
            }
            completed=true;Save();return 0;
        }
        catch(Exception error){errors.Add(error.ToString());Save();return 3;}
        void Save()
        {
            string path=Path.Combine(output,"diagnostic.json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new {schema="hlslperf.focused-cost-diagnostic.v1",developmentOnly=true,
                completed,processIndex,pid=Environment.ProcessId,startedUtc=started,recordedUtc=DateTimeOffset.UtcNow,
                device=tuner.DescribeDevice("per-arm:6_6-or-6_7"),deviceRemovalStatus=tuner.DeviceRemovalStatus,runtime,
                compilation=executor.CompilationEvidence,results,errors},JsonDefaults.Options));
            File.Move(path+".tmp",path,true);
        }
    }
}
