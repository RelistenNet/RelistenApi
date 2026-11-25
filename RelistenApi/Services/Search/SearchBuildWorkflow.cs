using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Relisten.Ingestion;
using Relisten.Normalization.Temporal;
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
        var snapshot = await Workflow.ExecuteActivityAsync(
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
                // The grace period can outlive this build.
                ParentClosePolicy = ParentClosePolicy.Abandon,
            }
        );
        await CatchUp(snapshot.StartedAt);
        return snapshot.Result;
    }

    private static async Task CatchUp(DateTime startedAt)
    {
        try
        {
            await Workflow.ExecuteActivityAsync(
                (SearchBuildActivities activities) => activities.CatchUp(startedAt),
                new ActivityOptions
                {
                    TaskQueue = IngestionQueues.Database,
                    // Release the build ID before the one-hour retirement timer fires,
                    // even if catch-up sits in the activity queue or runs slowly.
                    ScheduleToCloseTimeout = TimeSpan.FromMinutes(45),
                    HeartbeatTimeout = TimeSpan.FromSeconds(30),
                    CancellationType = ActivityCancellationType.WaitCancellationCompleted,
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                }
            );
        }
        catch (ActivityFailureException error) when (!TemporalException.IsCanceledException(error))
        {
            Workflow.Logger.LogWarning(error, "Search catch-up failed after publishing the index");
        }
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

internal sealed record SearchBuildSnapshot(SearchBuildResult Result, DateTime StartedAt);

internal sealed class SearchBuildActivities(
    DbService db,
    SearchIndexBuilder builder,
    ArtistSearchIndexer artists,
    ILogger<SearchBuildActivities> logger
)
{
    [Activity]
    public async Task<SearchBuildSnapshot> Build(string? playExportPath, string buildId)
    {
        using var heartbeat = new ActivityHeartbeat();
        var token = ActivityExecutionContext.Current.CancellationToken;
        var timer = Stopwatch.StartNew();
        // Use the primary to avoid recovery canceling a long read on a replica.
        // This snapshot holds back vacuum for the length of the build, acceptable once a day.
        await using var connection = db.CreatePrimaryConnection(
            SearchProjectionRows.BuildCommandTimeoutSeconds
        );
        await connection.OpenAsync(token);
        await using var snapshot = await SearchProjectionRows.BeginSnapshot(connection, token);
        var startedAt = await connection.ExecuteScalarAsync<DateTime>(
            new CommandDefinition(
                "SELECT transaction_timestamp()",
                transaction: snapshot,
                cancellationToken: token
            )
        );
        var plays = await SearchPlayTotals.Load(
            connection,
            snapshot,
            DateOnly.FromDateTime(DateTime.UtcNow),
            playExportPath,
            token
        );
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, snapshot),
            plays,
            token
        );
        var counts = await builder.Build(
            artists.AliasPrefix,
            buildId,
            projection.Documents(token),
            token
        );
        return new SearchBuildSnapshot(
            new SearchBuildResult(
                buildId,
                counts,
                timer.Elapsed.TotalSeconds,
                projection.PlayedFiles
            ),
            startedAt
        );
    }

    [Activity]
    public async Task RemoveRetired(string publishedBuildId)
    {
        // Keep the shared workflow ID until the activity observes cancellation.
        using var heartbeat = new ActivityHeartbeat();
        await builder.RemoveRetired(
            artists.AliasPrefix,
            publishedBuildId,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    [Activity(SearchReindexStep.ActivityName)]
    public async Task ReindexArtist(Guid artist)
    {
        using var heartbeat = new ActivityHeartbeat();
        var token = ActivityExecutionContext.Current.CancellationToken;

        // Record the request before resolving aliases. If it writes to the old
        // collections, a build that took its snapshot earlier will replay it.
        await using (var connection = db.CreateConnection(longTimeout: false, readOnly: false))
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO search_artist_reindexes (artist_id, requested_at)
                    SELECT id, clock_timestamp() FROM artists WHERE uuid = @artist
                    ON CONFLICT (artist_id) DO UPDATE SET requested_at = clock_timestamp()
                    """,
                    new { artist },
                    cancellationToken: token
                )
            );
        }

        await artists.Reindex(artist, token);
    }

    [Activity]
    public async Task CatchUp(DateTime startedAt)
    {
        using var heartbeat = new ActivityHeartbeat();
        var token = ActivityExecutionContext.Current.CancellationToken;
        IEnumerable<Guid> requested;
        await using (var connection = db.CreateConnection(longTimeout: false, readOnly: false))
        {
            requested = await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT a.uuid
                    FROM search_artist_reindexes r JOIN artists a ON a.id = r.artist_id
                    WHERE r.requested_at >= @startedAt
                    ORDER BY r.artist_id
                    """,
                    new { startedAt },
                    cancellationToken: token
                )
            );
        }

        foreach (var artist in requested)
        {
            try
            {
                await artists.Reindex(artist, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(
                    error,
                    "Could not catch up search for artist {Artist} after publication",
                    artist
                );
            }
        }
    }
}
