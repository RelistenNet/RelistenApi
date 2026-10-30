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
/// Refreshes derived artist/venue counts after committed catalog or normalization writes.
/// Each refresh is atomic and repeatable. Periodic reconciliation repairs changes made
/// outside these explicit boundaries, including direct admin SQL and failed import hooks.
/// </summary>
public sealed class ArtistVenueCounts(INormalizationDatabase db)
{
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Recomputes all artists with normalized show assignments or stored relationships.
    /// Existing zero-count groups are included so removed assignments clear stale counts.
    /// </summary>
    public async Task<ArtistVenueRefreshResult> RefreshAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        return await RefreshAsync(connection, null, cancellationToken);
    }

    /// <summary>
    /// Recomputes only the supplied artists. A later reconciliation repairs source changes
    /// committed after this refresh's snapshot, including an interrupted import hook.
    /// </summary>
    public async Task<ArtistVenueRefreshResult> RefreshArtistsAsync(
        IReadOnlyCollection<Guid> artists,
        CancellationToken cancellationToken = default
    )
    {
        if (artists.Count == 0)
        {
            return new(0, 0);
        }

        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);

        return await RefreshAsync(connection, artists.ToArray(), cancellationToken);
    }

    // Each call is an autocommitted statement, separate from the source write.
    private static Task<ArtistVenueRefreshResult> RefreshAsync(
        NpgsqlConnection connection,
        Guid[]? artistFilter,
        CancellationToken cancellationToken
    ) =>
        connection.QuerySingleAsync<ArtistVenueRefreshResult>(
            new CommandDefinition(
                """
                SELECT artists_refreshed AS "ArtistsRefreshed",
                       relationships_changed AS "RelationshipsChanged"
                FROM norm.refresh_artist_venues(@artistFilter::uuid[])
                """,
                new { artistFilter },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken
            )
        );
}
