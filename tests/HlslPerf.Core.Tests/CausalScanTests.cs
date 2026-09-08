using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class CausalScanTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(8388608)]
    public void VectorIoChangesOnlyIoDefineAndShaderIdentity(int count)
    {
        var fixture=UnifiedWorkloads.Fixture("scan",count,19,"extremes");
        var original=UnifiedWorkloads.Build(".",fixture,"internal-scan-single");
        var candidate=CausalScanWorkloads.Build(".",fixture,CausalScanWorkloads.VectorIo);
        Assert.Equal(original.Buffers.Select(b=>(b.Name,b.ByteLength,b.InitialData is null?null:ContentHash.Sha256(b.InitialData))),
            candidate.Buffers.Select(b=>(b.Name,b.ByteLength,b.InitialData is null?null:ContentHash.Sha256(b.InitialData))));
        Assert.Equal(original.Outputs,candidate.Outputs);Assert.Equal(original.Passes.Count,candidate.Passes.Count);
        for(int i=0;i<original.Shaders.Count;i++)
        {
            var a=original.Shaders[i];var b=candidate.Shaders[i];Assert.NotEqual(a.Id,b.Id);
            Assert.Equal("1",a.Defines["HLSLPERF_VECTOR_WIDTH"]);Assert.Equal("4",b.Defines["HLSLPERF_VECTOR_WIDTH"]);
            Assert.Equal(a.Defines.Where(p=>p.Key!="HLSLPERF_VECTOR_WIDTH"),b.Defines.Where(p=>p.Key!="HLSLPERF_VECTOR_WIDTH"));
            Assert.Equal(a with { Id=b.Id,Defines=b.Defines },b);
        }
        Assert.DoesNotContain(CausalScanWorkloads.VectorIo,UnifiedWorkloads.ScanImplementations);
    }
}
