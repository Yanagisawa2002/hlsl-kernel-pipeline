using Vortice.Direct3D12.Debug;

namespace HlslPerf.D3D12;

public sealed record UnifiedDebugMessage(string Severity, string Id, string Description);

public sealed record UnifiedDebugFilter(string[] AllowedCategories, string[] AllowedSeverities, string[] AllowedIds,
    string[] DeniedCategories, string[] DeniedSeverities, string[] DeniedIds)
{
    public bool Available { get; init; } = true;
    // The D3D12 default storage filter may suppress informational object-lifetime messages.
    // Category, ID or allow-list restrictions can hide errors and are never accepted here.
    public bool RetainsWarningsAndErrors => Available && AllowedCategories.Length == 0 && AllowedSeverities.Length == 0 &&
        AllowedIds.Length == 0 && DeniedCategories.Length == 0 && DeniedIds.Length == 0 &&
        DeniedSeverities.All(s => s is "Info" or "Message");
    public bool SameAs(UnifiedDebugFilter other) => Available == other.Available && AllowedCategories.SequenceEqual(other.AllowedCategories) &&
        AllowedSeverities.SequenceEqual(other.AllowedSeverities) && AllowedIds.SequenceEqual(other.AllowedIds) &&
        DeniedCategories.SequenceEqual(other.DeniedCategories) && DeniedSeverities.SequenceEqual(other.DeniedSeverities) &&
        DeniedIds.SequenceEqual(other.DeniedIds);
}

/// <summary>A quiescent debug-queue read. Missing, lost or changing messages and filters that can hide errors fail closed.</summary>
public sealed record UnifiedDebugSnapshot(bool Available, ulong MessageLimit, ulong StoredMessages,
    ulong RetrievableMessages, ulong StoredMessagesAfterRead, ulong DiscardedMessages,
    ulong DeniedByStorageFilter, bool Cleared, UnifiedDebugMessage[] Messages)
{
    public UnifiedDebugFilter? StorageFilter { get; init; }
    public UnifiedDebugFilter? RetrievalFilter { get; init; }
    public bool FiltersStable { get; init; }
    public int ErrorCount => Messages.Count(m => m.Severity is "Error" or "Corruption");
    public bool Passed => Available && DiscardedMessages == 0 && FiltersStable &&
        StorageFilter is { RetainsWarningsAndErrors: true } && RetrievalFilter is { RetainsWarningsAndErrors: true } &&
        (DeniedByStorageFilter == 0 || StorageFilter.DeniedSeverities.Length > 0) &&
        StoredMessages == RetrievableMessages && StoredMessages == StoredMessagesAfterRead &&
        RetrievableMessages == (ulong)Messages.Length && ErrorCount == 0;
}

public sealed partial class D3D12Tuner
{
    /// <summary>Call only after completion of the work being checked; preserve the returned snapshot before rejecting it.</summary>
    public UnifiedDebugSnapshot ReadUnifiedDebugSnapshot(bool clear = false)
    {
        using ID3D12InfoQueue? info = device.QueryInterfaceOrNull<ID3D12InfoQueue>();
        if (info is null) return new(false, 0, 0, 0, 0, 0, 0, false, []);
        var storage = DescribeFilter(info.GetStorageFilter());
        var retrieval = DescribeFilter(info.GetRetrievalFilter());
        ulong stored = info.NumStoredMessages, retrievable = info.NumStoredMessagesAllowedByRetrievalFilter;
        var messages = Enumerable.Range(0, checked((int)retrievable)).Select(index =>
        {
            Message message = info.GetMessage((ulong)index);
            return new UnifiedDebugMessage(message.Severity.ToString(), message.Id.ToString(), message.Description);
        }).ToArray();
        var snapshot = new UnifiedDebugSnapshot(true, info.MessageCountLimit, stored, retrievable,
            info.NumStoredMessages, info.NumMessagesDiscardedByMessageCountLimit,
            info.NumMessagesDeniedByStorageFilter, clear, messages)
        {
            StorageFilter = storage, RetrievalFilter = retrieval,
            FiltersStable = storage.SameAs(DescribeFilter(info.GetStorageFilter())) &&
                retrieval.SameAs(DescribeFilter(info.GetRetrievalFilter()))
        };
        if (clear) info.ClearStoredMessages();
        return snapshot;
    }

    /// <summary>Deliberately loses a warning and injects an error. Use a dedicated control device, never a measurement device.</summary>
    public (UnifiedDebugSnapshot Initial, UnifiedDebugSnapshot Overflow, UnifiedDebugSnapshot Error) RunUnifiedDebugQueueControls(
        Action<string, UnifiedDebugSnapshot> capture)
    {
        var initial = ReadUnifiedDebugSnapshot(true);
        capture("initial", initial);
        if (!initial.Passed) throw new InvalidDataException("Debug control needs a clean, available queue.");
        using ID3D12InfoQueue info = device.QueryInterface<ID3D12InfoQueue>();
        ulong originalLimit = info.MessageCountLimit;
        try
        {
            info.MessageCountLimit = 2;
            for (int i = 0; i < 3; i++)
                info.AddApplicationMessage(MessageSeverity.Warning, $"Expected overflow control {i}");
            var overflow = ReadUnifiedDebugSnapshot(true);
            capture("overflow", overflow);
            info.AddApplicationMessage(MessageSeverity.Error, "Expected error rejection control");
            var error = ReadUnifiedDebugSnapshot(true);
            capture("error", error);
            return (initial, overflow, error);
        }
        finally { info.MessageCountLimit = originalLimit; }
    }

    private static UnifiedDebugFilter DescribeFilter(InfoQueueFilter? filter) => filter is null
        ? new([], [], [], [], [], []) { Available = false }
        : new(
        (filter.AllowList.Categories ?? []).Select(x => x.ToString()).ToArray(),
        (filter.AllowList.Severities ?? []).Select(x => x.ToString()).ToArray(),
        (filter.AllowList.Ids ?? []).Select(x => x.ToString()).ToArray(),
        (filter.DenyList.Categories ?? []).Select(x => x.ToString()).ToArray(),
        (filter.DenyList.Severities ?? []).Select(x => x.ToString()).ToArray(),
        (filter.DenyList.Ids ?? []).Select(x => x.ToString()).ToArray());
}
