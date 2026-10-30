using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Normalization.Songs;

/// <summary>Derived count rows inserted or changed by explicit artist recounts.</summary>
public sealed record SongCountRefreshResult(int SongsChanged, int ImportedSongsChanged);

/// <summary>
/// Refreshes canonical recording counts and imported setlist counts after committed writes.
/// Each artist is one autocommitted statement, so full repairs never retain earlier artist
/// locks. Admin edits call the same SQL function inside their source transaction instead.
/// </summary>
public sealed class SongCounts(INormalizationDatabase db)
{
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Recounts every owner of canonical or imported songs, including songs now at zero.
    /// Completed artists remain committed if a later artist fails; rerunning is safe.
    /// There is no scheduled repair or queue.
    /// </summary>
    public async Task<SongCountRefreshResult> RefreshAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        var artists = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                SELECT s.artist_uuid FROM norm.songs s
                UNION
                SELECT a.uuid FROM public.setlist_songs s JOIN public.artists a ON a.id=s.artist_id
                """,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken
            )
        );
        return await RefreshAsync(connection, artists, cancellationToken);
    }

    /// <summary>
    /// Recounts only the supplied artists. Reuses the normalizer's per-artist catalogue lock;
    /// different artists remain concurrent, and ordinary readers take no advisory locks.
    /// Source work has already committed; a failed refresh needs an explicit retry.
    /// </summary>
    public async Task<SongCountRefreshResult> RefreshArtistsAsync(
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
        return await RefreshAsync(connection, artists, cancellationToken);
    }

    private static async Task<SongCountRefreshResult> RefreshAsync(
        NpgsqlConnection connection,
        IEnumerable<Guid> artists,
        CancellationToken cancellationToken
    )
    {
        var songsChanged = 0;
        var importedSongsChanged = 0;
        foreach (var artist in artists.Distinct())
        {
            var result = await connection.QuerySingleAsync<SongCountRefreshResult>(
                new CommandDefinition(
                    """
                    SELECT songs_changed AS "SongsChanged",imported_songs_changed AS "ImportedSongsChanged"
                    FROM norm.refresh_song_counts(@artist)
                    """,
                    new { artist },
                    commandTimeout: CommandTimeoutSeconds,
                    cancellationToken: cancellationToken
                )
            );
            songsChanged += result.SongsChanged;
            importedSongsChanged += result.ImportedSongsChanged;
        }
        return new(songsChanged, importedSongsChanged);
    }
}
