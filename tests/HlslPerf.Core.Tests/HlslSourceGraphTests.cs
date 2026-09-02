using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class HlslSourceGraphTests
{
    [Fact]
    public void CombinedHashTracksTransitiveIncludeContent()
    {
        string root = Path.Combine(Path.GetTempPath(), "hlslperf-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "include"));
        try
        {
            string shader = Path.Combine(root, "kernel.hlsl");
            string include = Path.Combine(root, "include", "shared.hlsli");
            File.WriteAllText(shader, "#include \"include/shared.hlsli\"\n[numthreads(1,1,1)] void Main() {}\n");
            File.WriteAllText(include, "#define VALUE 1\n");
            HlslSourceGraph before = HlslSourceGraph.Load(shader);

            File.WriteAllText(include, "#define VALUE 2\n");
            HlslSourceGraph after = HlslSourceGraph.Load(shader);

            Assert.Equal(2, before.Dependencies.Count);
            Assert.NotEqual(before.CombinedSha256, after.CombinedSha256);
            Assert.Contains(after.Dependencies, dependency => dependency.RelativePath == "include/shared.hlsli");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CyclicIncludesAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "hlslperf-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "first.hlsl");
            File.WriteAllText(first, "#include \"second.hlsli\"\n");
            File.WriteAllText(Path.Combine(root, "second.hlsli"), "#include \"first.hlsl\"\n");

            Assert.Throws<InvalidDataException>(() => HlslSourceGraph.Load(first));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
