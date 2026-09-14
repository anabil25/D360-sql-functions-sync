namespace D360.SqlFunctionsSync;

public record TaxAccount(string SourceKey, DateTimeOffset ModifiedUtc, string ParcelNumber, decimal Balance);

/// <summary>
/// Stand-in for the customer systems so the durable workflow can be validated without a
/// SQL Server or Dataverse environment. Replace the two bodies with Microsoft.Data.SqlClient
/// and the Dataverse Web API; the orchestration contract does not change.
/// </summary>
public static class SyncDataAccess
{
    private static readonly DateTimeOffset SeedUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static int TotalRows =>
        int.TryParse(Environment.GetEnvironmentVariable("SIMULATED_ROW_COUNT"), out var rows) ? rows : 1_000;

    private static int WriteLatencyMs =>
        int.TryParse(Environment.GetEnvironmentVariable("SIMULATED_WRITE_LATENCY_MS"), out var ms) ? ms : 25;

    /// <summary>Stands in for SELECT SYSUTCDATETIME(); fixes the upper bound of one run.</summary>
    public static Task<DateTimeOffset> GetWindowEndUtcAsync() =>
        Task.FromResult(DateTimeOffset.UtcNow);

    /// <summary>Stands in for the keyset delta query ordered by (ModifiedUtc, SourceKey).</summary>
    public static Task<IReadOnlyList<TaxAccount>> ReadPageAsync(
        SyncCursor cursor,
        DateTimeOffset windowEndUtc,
        int pageSize)
    {
        var page = Enumerable.Range(1, TotalRows)
            .Select(i => new TaxAccount(
                $"ACCOUNT-{i:D8}",
                SeedUtc.AddSeconds(i),
                $"PARCEL-{i:D8}",
                100m + i))
            .Where(row => new SyncCursor(row.ModifiedUtc, row.SourceKey).IsAfter(cursor)
                && row.ModifiedUtc <= windowEndUtc)
            .Take(pageSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<TaxAccount>>(page);
    }

    /// <summary>Stands in for one alternate-key UpsertMultiple request.</summary>
    public static Task UpsertAsync(IReadOnlyList<TaxAccount> rows) =>
        Task.Delay(rows.Count == 0 ? 0 : WriteLatencyMs);
}
