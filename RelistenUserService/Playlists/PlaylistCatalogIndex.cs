using Microsoft.EntityFrameworkCore;
using Relisten.Identifiers;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

/// <summary>
/// Derives a playlist's catalog facts from its live track entries: which artists it
/// contains, and its total catalog duration. Public listings filter and display them
/// without resolving tracks at read time.
/// </summary>
/// <remarks>
/// Entries store only typed track IDs. The catalog shares this database, so the facts
/// are rebuilt in the writer's transaction by joining <c>public.source_tracks</c>.
/// <para>
/// <c>catalog_indexed_revision</c> records the revision the facts describe. This build
/// brings it to the new revision in every write that creates one: create, copy and each
/// content or name batch. Rows where it differs from <c>revision</c> predate the index or
/// were edited by an older build during a rollout; <see cref="PlaylistCatalogIndexSweep"/>
/// repairs them. Publication, archiving and moderation do not change the revision and
/// leave the facts alone.
/// </para>
/// <para>
/// A track missing from the catalog adds no artist or duration. A catalog re-import that
/// changes a track's artist or duration reaches the playlist at its next content edit.
/// </para>
/// <para>
/// These methods write with raw SQL after <c>SaveChanges</c>. A tracked <c>Playlist</c>
/// in the same context keeps its old catalog values; do not read them from it afterwards.
/// </para>
/// </remarks>
internal static class PlaylistCatalogIndex
{
    /// <summary>
    /// Rebuilds the facts from the entries already saved in this transaction. The caller
    /// must hold the playlist's row lock so that no other writer changes them meanwhile.
    /// </summary>
    internal static async Task RefreshAsync(
        AccountsDbContext database,
        PlaylistId playlistId,
        CancellationToken cancellationToken
    )
    {
        var trackIds = await database
            .PlaylistEntries.Where(row =>
                row.PlaylistId == playlistId && !row.Deleted && row.TrackId != null
            )
            .Select(row => row.TrackId!)
            .ToArrayAsync(cancellationToken);
        // The entry check constraint admits only canonical track IDs.
        var trackUuids = trackIds.Select(id => TrackId.Parse(id).Value).ToArray();

        // Three statements under the caller's row lock: replace the artist rows, then set
        // the duration and stamp the revision. The stamp reads the row's own revision, the
        // one the saved entries belong to, so it cannot describe a revision it did not see.
        // The array keeps one element per entry: a repeated track counts each time.
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM user_data.playlist_artists WHERE playlist_id = {playlistId.Value};

            INSERT INTO user_data.playlist_artists (playlist_id, artist_uuid, track_count)
            SELECT {playlistId.Value}, artist.uuid, count(*)::integer
            FROM unnest({trackUuids}::uuid[]) AS entry(track_uuid)
            JOIN public.source_tracks AS track ON track.uuid = entry.track_uuid
            JOIN public.artists AS artist ON artist.id = track.artist_id
            GROUP BY artist.uuid;

            UPDATE user_data.playlists
            SET catalog_duration_seconds = coalesce((
                    SELECT sum(coalesce(track.duration, 0))::integer
                    FROM unnest({trackUuids}::uuid[]) AS entry(track_uuid)
                    JOIN public.source_tracks AS track ON track.uuid = entry.track_uuid
                ), 0),
                catalog_indexed_revision = revision
            WHERE id = {playlistId.Value};
            """,
            cancellationToken
        );
    }

    /// <summary>
    /// Records that a batch which changed only the name left the facts as they were. When
    /// the facts did not describe the previous revision, rebuilds them instead.
    /// </summary>
    internal static async Task AdvanceAfterNameEditAsync(
        AccountsDbContext database,
        PlaylistId playlistId,
        long previousRevision,
        CancellationToken cancellationToken
    )
    {
        var advanced = await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE user_data.playlists SET catalog_indexed_revision = revision
            WHERE id = {playlistId.Value} AND catalog_indexed_revision = {previousRevision}
            """,
            cancellationToken
        );
        if (advanced == 0)
        {
            await RefreshAsync(database, playlistId, cancellationToken);
        }
    }

    /// <summary>
    /// Rebuilds one playlist in a transaction of its own if its facts still lag its
    /// revision. Returns false if they were already current when the lock was granted.
    /// </summary>
    internal static async Task<bool> RepairAsync(
        AccountsDbContext database,
        PlaylistId playlistId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        // Wait behind any writer: writers hold this lock only for their own short
        // transaction, and the staleness check after the wait sees what they committed.
        // Repair takes no other lock, so it cannot deadlock with them.
        var stale = await database
            .Playlists.FromSqlInterpolated(
                $"""
                SELECT * FROM user_data.playlists
                WHERE id = {playlistId.Value} AND catalog_indexed_revision <> revision
                FOR UPDATE
                """
            )
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        if (stale.Length == 0)
        {
            return false;
        }

        await RefreshAsync(database, playlistId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// Repairs playlists whose catalog facts lag their revision, once per process start.
/// This backfills playlists created before the index, and those an older build edited
/// during a rollout once the last new pod starts. If two processes sweep at once, each
/// repair rechecks under the row lock, so the second finds nothing left to do.
/// </summary>
internal sealed class PlaylistCatalogIndexSweep(
    IServiceScopeFactory scopes,
    ILogger<PlaylistCatalogIndexSweep> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PlaylistId[] stale;
        try
        {
            stale = await StalePlaylistsAsync(stoppingToken);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            // Listings stay available with stale facts; the next process start retries.
            logger.LogWarning(error, "Could not find playlists with stale catalog facts.");
            return;
        }

        var repaired = 0;
        foreach (var id in stale)
        {
            if (await TryRepairAsync(id, stoppingToken))
            {
                repaired++;
            }
        }

        if (repaired > 0)
        {
            logger.LogInformation("Rebuilt catalog facts for {Count} playlists.", repaired);
        }
    }

    private async Task<PlaylistId[]> StalePlaylistsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        return await database
            .Playlists.AsNoTracking()
            .Where(row => row.CatalogIndexedRevision != row.Revision)
            .OrderBy(row => row.Id)
            .Select(row => row.Id)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>One failed playlist must not leave the rest of the sweep undone.</summary>
    private async Task<bool> TryRepairAsync(PlaylistId id, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
            return await PlaylistCatalogIndex.RepairAsync(database, id, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Could not rebuild catalog facts for {PlaylistId}.", id);
            return false;
        }
    }
}
