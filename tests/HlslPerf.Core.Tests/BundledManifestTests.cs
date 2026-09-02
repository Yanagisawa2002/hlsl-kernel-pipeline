using HlslPerf.Core;
using HlslPerf.Workloads;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class BundledManifestTests
{
    [Fact]
    public void EveryBundledManifestValidatesExpandsAndResolves()
    {
        string root = FindRepositoryRoot();
        string[] manifests = Directory.GetFiles(Path.Combine(root, "manifests"), "*.json")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(manifests);
        foreach (string path in manifests)
        {
            TuningManifest manifest = TuningManifest.Load(path);
            IReadOnlyList<KernelCandidate> candidates = CandidateGenerator.Expand(manifest);
            Assert.NotEmpty(candidates);
            Assert.NotNull(BuiltinWorkloads.Resolve(manifest));
            Assert.Contains(candidates, candidate =>
                candidate.Id == CandidateGenerator.ResolveBaseline(manifest, candidates).Id);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HlslKernelPipeline.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the HlslKernelPipeline repository root.");
    }
}
