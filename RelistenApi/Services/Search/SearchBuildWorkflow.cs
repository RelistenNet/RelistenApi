using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
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

internal sealed class SearchBuildActivities(
    DbService db,
    SearchIndexBuilder builder,
    ArtistSearchIndexer artists,
    IConfiguration configuration,
    ILogger<SearchBuildActivities> logger
)
{
    [Activity]
    public async Task<SearchBuildResult> Build(string? playExportPath, string buildId)
    {
        using var heartbeat = new ActivityHeartbeat();
        var token = ActivityExecutionContext.Current.CancellationToken;
        var (result, since) = await BuildSnapshot(playExportPath, buildId, token);
        await RefreshChanges(since, playExportPath, token);
        return result;
    }

    [Activity(SearchReindexStep.ActivityName)]
    public async Task ReindexArtist(Guid artist)
    {
        using var heartbeat = new ActivityHeartbeat();
        await artists.Reindex(
            artist,
            configuration["Search:PlaysExport"],
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    private async Task<(SearchBuildResult Result, DateTime Since)> BuildSnapshot(
        string? playExportPath,
        string buildId,
        CancellationToken token
    )
    {
        var timer = Stopwatch.StartNew();
        // Use the primary to avoid recovery canceling a long read on a replica.
        // This snapshot holds back vacuum for the length of the build, acceptable once a day.
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(token);
        await using var snapshot = await SearchProjectionRows.BeginSnapshot(connection, token);
        // Include short catalog transactions that began just before the snapshot
        // but did not commit until after it was taken.
        var since = await connection.ExecuteScalarAsync<DateTime>(
            new CommandDefinition(
                "SELECT transaction_timestamp() - interval '1 minute'",
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
        return (
            new SearchBuildResult(
                buildId,
                counts,
                timer.Elapsed.TotalSeconds,
                projection.PlayedFiles
            ),
            since
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

    private async Task RefreshChanges(
        DateTime since,
        string? playExportPath,
        CancellationToken cancellationToken
    )
    {
        IEnumerable<Guid> changed;
        try
        {
            // Read after publication in a new transaction, so reindexing sees
            // rows the completed build's snapshot could not see.
            await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
            changed = await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    ChangedArtistsSql,
                    new { since },
                    cancellationToken: cancellationToken
                )
            );
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Could not read catalog changes after search publication");
            return;
        }

        foreach (var artist in changed)
        {
            try
            {
                await artists.Reindex(artist, playExportPath, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    error,
                    "Could not catch up search for artist {Artist} after publication",
                    artist
                );
            }
        }
    }

    private const string ChangedArtistsSql = """
        WITH changed_venues AS (
            SELECT id FROM norm.venues WHERE updated_at >= @since
            UNION SELECT venue_id FROM norm.venue_names WHERE updated_at >= @since
        ), changed_artist_ids AS (
            SELECT id AS artist_id FROM artists WHERE updated_at >= @since
            UNION SELECT artist_id FROM shows WHERE updated_at >= @since
            UNION SELECT artist_id FROM sources WHERE updated_at >= @since OR created_at >= @since
            UNION SELECT artist_id FROM venues WHERE updated_at >= @since
            UNION
            SELECT s.artist_id FROM source_reviews r JOIN sources s ON s.id = r.source_id
            WHERE r.updated_at >= @since
            UNION
            SELECT s.artist_id FROM norm.show_venues v JOIN shows s ON s.uuid = v.show_uuid
            WHERE v.updated_at >= @since OR v.venue_id IN (SELECT id FROM changed_venues)
            UNION
            SELECT t.artist_id FROM norm.track_songs m JOIN source_tracks t ON t.uuid = m.track_uuid
            WHERE m.updated_at >= @since
        )
        SELECT a.uuid FROM changed_artist_ids c JOIN artists a ON a.id = c.artist_id
        UNION SELECT artist_uuid FROM norm.songs WHERE updated_at >= @since
        UNION SELECT artist_uuid FROM norm.song_aliases WHERE updated_at >= @since
        ORDER BY 1
        """;
}
