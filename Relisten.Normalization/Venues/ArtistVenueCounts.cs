using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Normalization.Venues;

/// <summary>The stored relationship rows changed by one count refresh.</summary>
public sealed record ArtistVenueRefreshResult(int ArtistsRefreshed, int RelationshipsChanged);

/// <summary>
/// Refreshes derived artist/venue counts after catalog writes. Pending artists are durable:
/// each database refresh consumes its queue rows and updates counts in one short transaction.
/// A failed or canceled statement leaves that work available for a later retry.
/// </summary>
public sealed class ArtistVenueCounts(INormalizationDatabase db)
{
    private const int BatchSize = 100;
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Requests a refresh for the supplied artists. Null requests reconciliation of all
    /// artists with normalized assignments or stored relationships; an empty list does nothing.
    /// This statement commits before any subsequent refresh starts.
    /// </summary>
    public async Task RequestAsync(
        IReadOnlyCollection<Guid>? artists,
        CancellationToken cancellationToken = default
    )
    {
        if (artists is { Count: 0 })
        {
            return;
        }

        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await RequestAsync(connection, artists?.Distinct().ToArray(), cancellationToken);
    }

    /// <summary>
    /// Refreshes at most one pending batch. Busy artists are skipped, not waited on, and
    /// remain pending. Callers decide how many batches to drain in a background run.
    /// </summary>
    public async Task<ArtistVenueRefreshResult> RefreshPendingAsync(
        int batchSize = BatchSize,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        return await RefreshBatchAsync(connection, null, batchSize, cancellationToken);
    }

    /// <summary>
    /// Requests and refreshes only the supplied artists. One pass through bounded batches
    /// keeps an import hook finite; busy or newly dirtied artists remain for the background retry.
    /// Completed batches stay committed if a later batch fails or is canceled.
    /// </summary>
    public async Task<ArtistVenueRefreshResult> RefreshArtistsAsync(
        IReadOnlyCollection<Guid> artists,
        CancellationToken cancellationToken = default
    )
    {
        var artistIds = artists.Distinct().ToArray();
        if (artistIds.Length == 0)
        {
            return new(0, 0);
        }

        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await RequestAsync(connection, artistIds, cancellationToken);

        var artistsRefreshed = 0;
        var relationshipsChanged = 0;
        foreach (var batch in artistIds.Chunk(BatchSize))
        {
            var result = await RefreshBatchAsync(connection, batch, BatchSize, cancellationToken);
            artistsRefreshed += result.ArtistsRefreshed;
            relationshipsChanged += result.RelationshipsChanged;
        }

        return new(artistsRefreshed, relationshipsChanged);
    }

    private static Task RequestAsync(
        NpgsqlConnection connection,
        Guid[]? artists,
        CancellationToken cancellationToken
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT norm.request_artist_venue_refresh(@artists::uuid[])",
                new { artists },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken
            )
        );

    // Each call is an autocommitted statement. Queue locks belong to that transaction,
    // rather than the source import or an entire multi-batch reconciliation.
    private static Task<ArtistVenueRefreshResult> RefreshBatchAsync(
        NpgsqlConnection connection,
        Guid[]? artistFilter,
        int batchSize,
        CancellationToken cancellationToken
    ) =>
        connection.QuerySingleAsync<ArtistVenueRefreshResult>(
            new CommandDefinition(
                """
                SELECT artists_refreshed AS "ArtistsRefreshed",
                       relationships_changed AS "RelationshipsChanged"
                FROM norm.refresh_artist_venues(@artistFilter::uuid[], @batchSize)
                """,
                new { artistFilter, batchSize },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken
            )
        );
}
