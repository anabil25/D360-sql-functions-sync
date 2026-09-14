using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;

namespace D360.SqlFunctionsSync;

public static class SqlToDataverseSync
{
    public const string OrchestratorName = nameof(SqlToDataverseSyncOrchestrator);

    private static readonly TaskOptions PageRetry = TaskOptions.FromRetryPolicy(
        new RetryPolicy(maxNumberOfAttempts: 4, firstRetryInterval: TimeSpan.FromSeconds(2)));

    /// <summary>
    /// Processes one page per replay: read, write, checkpoint, then ContinueAsNew.
    /// History stays bounded no matter how many rows the window contains.
    /// </summary>
    [Function(OrchestratorName)]
    public static async Task<SyncSummary> SqlToDataverseSyncOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        SyncInput input = context.GetInput<SyncInput>() ?? SyncInput.ForPartition("default");
        ILogger logger = context.CreateReplaySafeLogger(nameof(SqlToDataverseSyncOrchestrator));
        var checkpointId = new EntityInstanceId(nameof(SyncCheckpoint), input.Partition);

        // Opening a window: adopt the committed cursor and freeze the upper bound.
        if (input.WindowEndUtc is null)
        {
            SyncCursor committed = await context.Entities.CallEntityAsync<SyncCursor>(
                checkpointId, nameof(SyncCheckpoint.Get));

            DateTimeOffset windowEnd = await context.CallActivityAsync<DateTimeOffset>(
                nameof(CaptureWindow), input.Partition);

            input = input with { Cursor = committed, WindowEndUtc = windowEnd };
            logger.LogInformation(
                "Window opened for {Partition} from {Cursor} through {WindowEnd}.",
                input.Partition, input.Cursor.ModifiedUtc, windowEnd);
        }

        PageResult page = await context.CallActivityAsync<PageResult>(
            nameof(ProcessPage),
            new PageCommand(input.Partition, input.Cursor, input.WindowEndUtc.Value, input.PageSize),
            PageRetry);

        // Checkpoint only after the page is durably written.
        if (page.RowsProcessed > 0)
        {
            await context.Entities.CallEntityAsync(checkpointId, nameof(SyncCheckpoint.Advance), page);
        }

        input = input with
        {
            Cursor = page.NextCursor,
            PagesProcessed = input.PagesProcessed + (page.RowsProcessed > 0 ? 1 : 0),
            RowsProcessed = input.RowsProcessed + page.RowsProcessed
        };

        if (page.HasMore)
        {
            context.ContinueAsNew(input);
            return new SyncSummary(input.Partition, input.PagesProcessed, input.RowsProcessed, input.Cursor);
        }

        // Window drained. Wait and reopen when a recurrence is configured, otherwise finish.
        if (input.RecurrenceSeconds is > 0)
        {
            await context.CreateTimer(
                context.CurrentUtcDateTime.AddSeconds(input.RecurrenceSeconds.Value),
                CancellationToken.None);

            context.ContinueAsNew(input with { WindowEndUtc = null, PagesProcessed = 0, RowsProcessed = 0 });
            return new SyncSummary(input.Partition, input.PagesProcessed, input.RowsProcessed, input.Cursor);
        }

        logger.LogInformation(
            "Sync complete for {Partition}: {Rows} rows in {Pages} pages.",
            input.Partition, input.RowsProcessed, input.PagesProcessed);

        return new SyncSummary(input.Partition, input.PagesProcessed, input.RowsProcessed, input.Cursor);
    }

    [Function(nameof(CaptureWindow))]
    public static Task<DateTimeOffset> CaptureWindow([ActivityTrigger] string partition) =>
        SyncDataAccess.GetWindowEndUtcAsync();

    [Function(nameof(ProcessPage))]
    public static async Task<PageResult> ProcessPage(
        [ActivityTrigger] PageCommand command,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ProcessPage));

        IReadOnlyList<TaxAccount> rows = await SyncDataAccess.ReadPageAsync(
            command.Cursor, command.WindowEndUtc, command.PageSize);

        if (rows.Count == 0)
        {
            return new PageResult(command.Cursor, 0, HasMore: false);
        }

        await SyncDataAccess.UpsertAsync(rows);

        TaxAccount last = rows[^1];
        logger.LogInformation(
            "Wrote {Count} rows for {Partition} through {SourceKey}.",
            rows.Count, command.Partition, last.SourceKey);

        return new PageResult(
            new SyncCursor(last.ModifiedUtc, last.SourceKey),
            rows.Count,
            HasMore: rows.Count == command.PageSize);
    }

    [Function(nameof(StartSync))]
    public static async Task<HttpResponseData> StartSync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sync/{partition?}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string? partition,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(StartSync));
        partition ??= "tax-account";

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int pageSize = int.TryParse(query["pageSize"], out var size) ? size : 100;
        int? recurrence = int.TryParse(query["recurrenceSeconds"], out var seconds) ? seconds : null;

        // Fixed instance ID keeps one sync per partition; a running instance is not restarted.
        var options = new StartOrchestrationOptions(InstanceId: $"sync-{partition}");
        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            OrchestratorName,
            SyncInput.ForPartition(partition, pageSize, recurrence),
            options);

        logger.LogInformation("Started sync orchestration '{InstanceId}'.", instanceId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}
