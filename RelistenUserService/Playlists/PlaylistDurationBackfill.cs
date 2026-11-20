using Microsoft.EntityFrameworkCore;
using RelistenUserService.Library;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

/// <summary>Fills missing duration snapshots while ordinary playlist edits continue.</summary>
internal sealed class PlaylistDurationBackfill(
    AccountsDbContext database,
    PlaylistTrackDurations durations,
    TimeProvider clock
)
{
    internal async Task<(int UpdatedPlaylists, int IncompletePlaylists)> RunAsync(
        CancellationToken cancellationToken
    )
    {
        var pending = await database
            .Playlists.AsNoTracking()
            .OrderBy(playlist => playlist.Id)
            .Select(playlist => new { playlist.Id, playlist.OwnerId })
            .ToArrayAsync(cancellationToken);
        var updated = 0;
        var incomplete = 0;
        foreach (var playlist in pending)
        {
            var ids = await database
                .PlaylistEntries.AsNoTracking()
                .Where(entry =>
                    entry.PlaylistId == playlist.Id
                    && !entry.Deleted
                    && entry.TrackId != null
                    && entry.TrackDurationSeconds == null
                )
                .Select(entry => entry.TrackId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var resolved = await durations.ResolveAsync(ids, cancellationToken, backfill: true);
            var result = await ApplyAsync(
                playlist.OwnerId,
                playlist.Id,
                resolved,
                cancellationToken
            );
            updated += result.Updated ? 1 : 0;
            incomplete += result.Incomplete ? 1 : 0;
            database.ChangeTracker.Clear();
        }

        return (updated, incomplete);
    }

    private async Task<(bool Updated, bool Incomplete)> ApplyAsync(
        UserId owner,
        PlaylistId id,
        IReadOnlyDictionary<string, int> resolved,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        if (
            !await PlaylistAccountLock.AcquireAsync(
                database,
                owner,
                requireActive: false,
                cancellationToken
            )
        )
        {
            return (false, false);
        }

        var now = clock.GetUtcNow();
        var stateStore = new LibraryStateStore(database);
        var state = await stateStore.GetOrCreateForUpdateAsync(owner, now, cancellationToken);
        var playlist = await PlaylistWriteService.LockOwnedPlaylistAsync(
            database,
            owner,
            id,
            cancellationToken
        );

        var previousDuration = playlist.DurationSeconds;
        var previousTrackCount = playlist.TrackCount;
        // An edit may have committed during resolution. Count the current live entries under the lock.
        var entries = await database
            .PlaylistEntries.Where(entry =>
                entry.PlaylistId == id && !entry.Deleted && entry.TrackId != null
            )
            .ToArrayAsync(cancellationToken);
        var updated = false;
        foreach (var entry in entries)
        {
            if (
                entry.TrackDurationSeconds is null
                && resolved.TryGetValue(entry.TrackId!, out var seconds)
            )
            {
                entry.TrackDurationSeconds = seconds;
                updated = true;
            }
        }

        var knownCount = entries.Count(entry => entry.TrackDurationSeconds.HasValue);
        var knownSeconds = entries.Sum(entry => (long)(entry.TrackDurationSeconds ?? 0));
        updated |=
            knownCount != playlist.TracksWithDuration
            || knownSeconds != playlist.KnownDurationSeconds;
        playlist.TrackCount = entries.Length;
        playlist.TracksWithDuration = knownCount;
        playlist.KnownDurationSeconds = knownSeconds;
        if (updated)
        {
            if (
                playlist.TrackCount != previousTrackCount
                || playlist.DurationSeconds != previousDuration
            )
            {
                stateStore.Append(state, PlaylistWriteService.SummaryChange(playlist, now));
            }
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (updated, playlist.DurationSeconds is null);
    }
}
