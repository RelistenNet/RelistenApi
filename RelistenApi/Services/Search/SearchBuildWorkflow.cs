using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using Relisten.Ingestion;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Relisten.Search;

public sealed record SearchBuildResult(
    string BuildId,
    Dictionary<string, long> Counts,
    long Rejected,
    double BuildSeconds,
    string[] PreviousCollections,
    long PlayedFiles
);

[Workflow]
public sealed class SearchBuildWorkflow
{
    public const string Id = "catalog-search-build";

    [WorkflowRun]
    public async Task<SearchBuildResult> RunAsync(string? playExportPath)
    {
        var result = await Workflow.ExecuteActivityAsync(
            (SearchBuildActivities activities) =>
                activities.Build(playExportPath, Workflow.NewGuid().ToString("N")),
            new ActivityOptions
            {
                TaskQueue = IngestionQueues.Database,
                StartToCloseTimeout = TimeSpan.FromHours(2),
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                // Rerun a failed build with a fresh collection name.
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            }
        );

        if (result.PreviousCollections.Length > 0)
        {
            await Workflow.StartChildWorkflowAsync(
                (SearchRetireWorkflow child) => child.RunAsync(result.PreviousCollections),
                new ChildWorkflowOptions
                {
                    Id = $"catalog-search-retire-{result.BuildId}",
                    ParentClosePolicy = ParentClosePolicy.Abandon,
                }
            );
        }

        return result;
    }
}

[Workflow]
public sealed class SearchRetireWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string[] collections)
    {
        await Workflow.DelayAsync(TimeSpan.FromHours(1));
        await Workflow.ExecuteActivityAsync(
            (SearchBuildActivities activities) => activities.RemoveRetired(collections),
            new ActivityOptions
            {
                TaskQueue = IngestionQueues.Database,
                StartToCloseTimeout = TimeSpan.FromMinutes(5),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
            }
        );
    }
}

internal sealed class SearchBuildActivities(DbService db, SearchIndexBuilder builder)
{
    private const string AliasPrefix = "catalog";

    [Activity]
    public async Task<SearchBuildResult> Build(string? playExportPath, string buildId)
    {
        using var heartbeat = new ActivityHeartbeat();
        var token = ActivityExecutionContext.Current.CancellationToken;
        var timer = Stopwatch.StartNew();

        // A build reads one consistent catalog without taking any write locks.
        // Use the primary connection to avoid replica recovery canceling a long scan.
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(token);
        await using var snapshot = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            token
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: snapshot,
                cancellationToken: token
            )
        );

        var through = DateOnly.FromDateTime(DateTime.UtcNow);
        var plays = await SearchPlayTotals.Load(
            connection,
            snapshot,
            through,
            playExportPath,
            token
        );
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, snapshot),
            plays,
            token
        );
        var (counts, previousCollections) = await builder.Build(
            AliasPrefix,
            buildId,
            new SearchBuildDocuments(
                projection.Reviews(token),
                projection.Performances(token),
                projection.Recordings(token),
                projection.Entities(token)
            ),
            token
        );

        return new SearchBuildResult(
            buildId,
            counts,
            Rejected: 0,
            timer.Elapsed.TotalSeconds,
            previousCollections,
            projection.PlayedFiles
        );
    }

    [Activity]
    public Task RemoveRetired(string[] collections) =>
        builder.RemoveRetired(
            AliasPrefix,
            collections,
            ActivityExecutionContext.Current.CancellationToken
        );
}
