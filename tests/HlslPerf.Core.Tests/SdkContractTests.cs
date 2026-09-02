using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class SdkContractTests
{
    [Fact]
    public void PublicSdkVersionMatchesCoreAssemblyVersion()
    {
        Version assemblyVersion = typeof(HlslPerfSdk).Assembly.GetName().Version!;

        Assert.Equal(HlslPerfSdk.Version, assemblyVersion.ToString(3));
    }
}
