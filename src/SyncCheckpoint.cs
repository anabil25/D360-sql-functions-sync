using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Entities;

namespace D360.SqlFunctionsSync;

/// <summary>
/// Durable entity holding the committed cursor for one partition. State lives in the
/// Durable Task Scheduler, so no application-managed store is required.
/// </summary>
public class SyncCheckpoint
{
    public SyncCursor Cursor { get; set; } = SyncCursor.Start;

    public DateTimeOffset LastCommittedUtc { get; set; }

    public int RowsCommitted { get; set; }

    public SyncCursor Get() => Cursor;

    /// <summary>Advances only forward, so a replayed page can never move the cursor backwards.</summary>
    public void Advance(PageResult page)
    {
        if (!page.NextCursor.IsAfter(Cursor))
        {
            return;
        }

        Cursor = page.NextCursor;
        RowsCommitted += page.RowsProcessed;
        LastCommittedUtc = DateTimeOffset.UtcNow;
    }

    public void Reset()
    {
        Cursor = SyncCursor.Start;
        RowsCommitted = 0;
        LastCommittedUtc = default;
    }

    [Function(nameof(SyncCheckpoint))]
    public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher) =>
        dispatcher.DispatchAsync<SyncCheckpoint>();
}
