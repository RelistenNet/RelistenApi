using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Ingestion;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Search;

public sealed record SearchBuildResult(
    string BuildId,
    Dictionary<string, long> Counts,
    double BuildSeconds,
    long PlayedFiles
);

[Workflow]
public sealed class SearchBuildWorkflow
{
    // Temporal uniqueness on this ID is the sweep lock. Retirement cleanup reuses
    // it, so every build must start with this ID to protect its unaliased collections.
    public const string Id = "catalog-search-build";

    [WorkflowRun]
    public async Task<SearchBuildResult> RunAsync(string? playExportPath)
    {
        var buildId = Workflow.NewGuid().ToString("N");
        var result = await Workflow.ExecuteActivityAsync(
            (SearchBuildActivities activities) => activities.Build(playExportPath, buildId),
            new ActivityOptions
            {
                TaskQueue = IngestionQueues.Database,
                StartToCloseTimeout = TimeSpan.FromHours(2),
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            }
        );

        await Workflow.StartChildWorkflowAsync(
            (SearchRetireWorkflow child) => child.RunAsync(buildId),
            new ChildWorkflowOptions
            {
                Id = $"catalog-search-retire-{buildId}",
                // The build returns at publication. The timer must survive until cleanup is due.
                ParentClosePolicy = ParentClosePolicy.Abandon,
            }
        );
        return result;
    }
}

[Workflow]
public sealed class SearchRetireWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string publishedBuildId)
    {
        // Give searches that resolved the previous aliases time to finish.
        await Workflow.DelayAsync(TimeSpan.FromHours(1));

        try
        {
            // A build's collections are unaliased until publication. Reuse its
            // workflow ID so the sweep cannot run during a newer build.
            await Workflow.ExecuteChildWorkflowAsync(
                (SearchCleanupWorkflow cleanup) => cleanup.RunAsync(publishedBuildId),
                new ChildWorkflowOptions { Id = SearchBuildWorkflow.Id }
            );
        }
        catch (WorkflowAlreadyStartedException)
        {
            // A build already owns the collections and handles cleanup.
        }
    }
}

[Workflow]
public sealed class SearchCleanupWorkflow
{
    [WorkflowRun]
    public Task RunAsync(string publishedBuildId) =>
        Workflow.ExecuteActivityAsync(
            (SearchBuildActivities activities) => activities.RemoveRetired(publishedBuildId),
            new ActivityOptions
            {
                TaskQueue = IngestionQueues.Database,
                StartToCloseTimeout = TimeSpan.FromMinutes(10),
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            }
        );
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

        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(token);
        await using var snapshot = await OpenCatalogSnapshot(connection, token);

        var projection = await LoadProjection(connection, snapshot, playExportPath, token);
        var counts = await builder.Build(AliasPrefix, buildId, projection.Documents(token), token);
        return new SearchBuildResult(
            buildId,
            counts,
            timer.Elapsed.TotalSeconds,
            projection.PlayedFiles
        );
    }

    [Activity]
    public async Task RemoveRetired(string publishedBuildId)
    {
        // Keep the shared workflow ID until the activity observes cancellation.
        using var heartbeat = new ActivityHeartbeat();
        await builder.RemoveRetired(
            AliasPrefix,
            publishedBuildId,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    private static async Task<NpgsqlTransaction> OpenCatalogSnapshot(
        NpgsqlConnection connection,
        CancellationToken token
    )
    {
        // Use the primary to avoid recovery canceling a long read on a replica.
        // This snapshot holds back vacuum for the length of the build, acceptable once a day.
        var snapshot = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: snapshot,
                    cancellationToken: token
                )
            );
            return snapshot;
        }
        catch
        {
            await snapshot.DisposeAsync();
            throw;
        }
    }

    private static async Task<CatalogSearchProjection> LoadProjection(
        NpgsqlConnection connection,
        NpgsqlTransaction snapshot,
        string? playExportPath,
        CancellationToken token
    )
    {
        var through = DateOnly.FromDateTime(DateTime.UtcNow);
        var plays = await SearchPlayTotals.Load(
            connection,
            snapshot,
            through,
            playExportPath,
            token
        );
        return await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, snapshot),
            plays,
            token
        );
    }
}
