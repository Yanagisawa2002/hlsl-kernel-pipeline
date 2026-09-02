using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class WorkloadCatalogTests
{
    [Fact]
    public void CatalogRejectsDuplicateIdsAcrossProviders()
    {
        WorkloadCatalog catalog = new();
        catalog.Register(new FakeProvider("duplicate-v1"), "first.dll");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            catalog.Register(new FakeProvider("duplicate-v1"), "second.dll"));

        Assert.Contains("first.dll", exception.Message);
        Assert.Contains("second.dll", exception.Message);
    }

    [Fact]
    public void CatalogRejectsProviderReturningDifferentWorkloadId()
    {
        WorkloadCatalog catalog = new();
        catalog.Register(new FakeProvider("requested-v1", "different-v1"));

        Assert.Throws<InvalidDataException>(() => catalog.Resolve("requested-v1"));
    }

    [Fact]
    public void DirectoryDiscoveryIgnoresManagedAssembliesWithoutProviders()
    {
        IReadOnlyList<IKernelWorkloadProvider> providers = WorkloadPluginLoader.LoadIfPresent(
            typeof(IKernelWorkload).Assembly.Location);

        Assert.Empty(providers);
    }

    private sealed class FakeProvider : IKernelWorkloadProvider
    {
        private readonly string returnedId;

        public FakeProvider(string exportedId, string? returnedId = null)
        {
            WorkloadIds = [exportedId];
            this.returnedId = returnedId ?? exportedId;
        }

        public IReadOnlyCollection<string> WorkloadIds { get; }

        public IKernelWorkload Create(string workloadId) => new FakeWorkload(returnedId);
    }

    private sealed class FakeWorkload(string id) : IKernelWorkload
    {
        public string Id { get; } = id;

        public KernelExecutionPlan Build(TuningManifest manifest, KernelCandidate candidate) =>
            throw new NotSupportedException();
    }
}
