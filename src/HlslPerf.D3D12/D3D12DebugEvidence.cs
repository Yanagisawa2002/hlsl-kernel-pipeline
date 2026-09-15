using Vortice.Direct3D12.Debug;

namespace HlslPerf.D3D12;

public sealed record UnifiedDebugMessage(string Severity, string Id, string Description);

/// <summary>A quiescent debug-queue read. Missing, filtered, lost or changing messages fail closed.</summary>
public sealed record UnifiedDebugSnapshot(bool Available, ulong MessageLimit, ulong StoredMessages,
    ulong RetrievableMessages, ulong StoredMessagesAfterRead, ulong DiscardedMessages,
    ulong DeniedByStorageFilter, bool Cleared, UnifiedDebugMessage[] Messages)
{
    public int ErrorCount => Messages.Count(m => m.Severity is "Error" or "Corruption");
    public bool Passed => Available && DiscardedMessages == 0 && DeniedByStorageFilter == 0 &&
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
        ulong stored = info.NumStoredMessages, retrievable = info.NumStoredMessagesAllowedByRetrievalFilter;
        var messages = Enumerable.Range(0, checked((int)retrievable)).Select(index =>
        {
            Message message = info.GetMessage((ulong)index);
            return new UnifiedDebugMessage(message.Severity.ToString(), message.Id.ToString(), message.Description);
        }).ToArray();
        var snapshot = new UnifiedDebugSnapshot(true, info.MessageCountLimit, stored, retrievable,
            info.NumStoredMessages, info.NumMessagesDiscardedByMessageCountLimit,
            info.NumMessagesDeniedByStorageFilter, clear, messages);
        if (clear) info.ClearStoredMessages();
        return snapshot;
    }

    /// <summary>Deliberately loses a warning and injects an error. Use a dedicated control device, never a measurement device.</summary>
    public (UnifiedDebugSnapshot Initial, UnifiedDebugSnapshot Overflow, UnifiedDebugSnapshot Error) RunUnifiedDebugQueueControls()
    {
        var initial = ReadUnifiedDebugSnapshot(true);
        if (!initial.Passed) throw new InvalidDataException("Debug control needs a clean, available queue.");
        using ID3D12InfoQueue info = device.QueryInterface<ID3D12InfoQueue>();
        ulong originalLimit = info.MessageCountLimit;
        try
        {
            info.MessageCountLimit = 2;
            for (int i = 0; i < 3; i++)
                info.AddApplicationMessage(MessageSeverity.Warning, $"Expected overflow control {i}");
            var overflow = ReadUnifiedDebugSnapshot(true);
            info.AddApplicationMessage(MessageSeverity.Error, "Expected error rejection control");
            var error = ReadUnifiedDebugSnapshot(true);
            return (initial, overflow, error);
        }
        finally { info.MessageCountLimit = originalLimit; }
    }
}
