using HlslPerf.D3D12;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class DebugEvidenceTests
{
    private static readonly UnifiedDebugSnapshot Warning = new(true, 1024, 1, 1, 1, 0, 0, true,
        [new("Warning", "CreateResourceStateIgnored", "Retained warning")])
    { StorageFilter = new([], [], [], [], [], []), RetrievalFilter = new([], [], [], [], [], []), FiltersStable = true };

    [Fact]
    public void CompleteWarningIsRetainedAndAccepted() => Assert.True(Warning.Passed);

    [Fact]
    public void LostOrFilteredMessagesCannotBeReportedAsClean()
    {
        Assert.False((Warning with { DiscardedMessages = 1 }).Passed);
        Assert.False((Warning with { DeniedByStorageFilter = 1 }).Passed);
        Assert.False((Warning with { StoredMessages = 2, StoredMessagesAfterRead = 2 }).Passed);
        Assert.False((Warning with { StoredMessagesAfterRead = 2 }).Passed);
        Assert.False((Warning with { Messages = [] }).Passed);
        Assert.False((Warning with { Available = false }).Passed);
        Assert.False((Warning with { FiltersStable = false }).Passed);
    }

    [Fact]
    public void OnlyRecordedInformationalSeverityFilteringIsAccepted()
    {
        var infoOnly = new UnifiedDebugFilter([], [], [], [], ["Info"], []);
        Assert.True((Warning with { StorageFilter = infoOnly, DeniedByStorageFilter = 119 }).Passed);
        Assert.False((Warning with { StorageFilter = infoOnly with { DeniedSeverities = ["Error"] } }).Passed);
        Assert.False((Warning with { StorageFilter = infoOnly with { DeniedSeverities = ["Warning"] } }).Passed);
        Assert.False((Warning with { StorageFilter = infoOnly with { DeniedIds = ["Application"] } }).Passed);
        Assert.False((Warning with { StorageFilter = infoOnly with { AllowedSeverities = ["Warning"] } }).Passed);
        Assert.False((Warning with { StorageFilter = infoOnly with { Available = false } }).Passed);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Corruption")]
    public void DriverErrorsFailEvenWhenNoMessagesAreLost(string severity)
    {
        var report = Warning with { Messages = [new(severity, "Application", "Injected test error")] };
        Assert.Equal(1, report.ErrorCount);
        Assert.False(report.Passed);
    }
}
