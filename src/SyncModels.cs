namespace D360.SqlFunctionsSync;

/// <summary>Ordered position in the source table. Ties on timestamp are broken by key.</summary>
public record SyncCursor(DateTimeOffset ModifiedUtc, string SourceKey)
{
    public static readonly SyncCursor Start = new(DateTimeOffset.MinValue, string.Empty);

    public bool IsAfter(SyncCursor other) =>
        ModifiedUtc != other.ModifiedUtc
            ? ModifiedUtc > other.ModifiedUtc
            : string.CompareOrdinal(SourceKey, other.SourceKey) > 0;
}

/// <summary>Orchestration state. Stays small so it never approaches the 1 MB payload limit.</summary>
public record SyncInput(
    string Partition,
    SyncCursor Cursor,
    DateTimeOffset? WindowEndUtc = null,
    int PageSize = 100,
    int? RecurrenceSeconds = null,
    int PagesProcessed = 0,
    int RowsProcessed = 0)
{
    public static SyncInput ForPartition(string partition, int pageSize = 100, int? recurrenceSeconds = null) =>
        new(partition, SyncCursor.Start, PageSize: pageSize, RecurrenceSeconds: recurrenceSeconds);
}

public record PageCommand(string Partition, SyncCursor Cursor, DateTimeOffset WindowEndUtc, int PageSize);

public record PageResult(SyncCursor NextCursor, int RowsProcessed, bool HasMore);

public record SyncSummary(string Partition, int PagesProcessed, int RowsProcessed, SyncCursor Cursor);
