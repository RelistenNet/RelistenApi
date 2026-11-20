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
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var liveTracks = database
            .PlaylistEntries.AsNoTracking()
            .Where(entry => !entry.Deleted && entry.TrackId != null);
        // After a rollback, older writers can remove tracks without updating the total.
        var pending = await database
            .Playlists.AsNoTracking()
            .Select(playlist => new
            {
                playlist.Id,
                playlist.OwnerId,
                TotalIsStale = liveTracks
                    .Where(entry => entry.PlaylistId == playlist.Id)
                    .Sum(entry => (long)(entry.TrackDurationSeconds ?? 0))
                    != playlist.DurationSeconds,
                HasMissingDuration = liveTracks.Any(entry =>
                    entry.PlaylistId == playlist.Id && entry.TrackDurationSeconds == null
                ),
            })
            .Where(playlist => playlist.TotalIsStale || playlist.HasMissingDuration)
            .OrderBy(playlist => playlist.Id)
            .ToArrayAsync(cancellationToken);

        var updated = 0;
        foreach (var playlist in pending)
        {
            var ids = await liveTracks
                .Where(entry =>
                    entry.PlaylistId == playlist.Id && entry.TrackDurationSeconds == null
                )
                .Select(entry => entry.TrackId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var resolved = await durations.ResolveAsync(ids, cancellationToken, backfill: true);
            if (resolved.Count == 0 && !playlist.TotalIsStale)
            {
                continue;
            }

            if (await ApplyAsync(playlist.OwnerId, playlist.Id, resolved, cancellationToken))
            {
                updated++;
            }
            database.ChangeTracker.Clear();
        }

        return updated;
    }

    private async Task<bool> ApplyAsync(
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
            return false;
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

        // An edit may have committed during resolution. Use the live entries under the lock.
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

        var total = entries.Sum(entry => (long)(entry.TrackDurationSeconds ?? 0));
        if (total != playlist.DurationSeconds)
        {
            playlist.DurationSeconds = total;
            stateStore.Append(state, PlaylistWriteService.SummaryChange(playlist, now));
            updated = true;
        }

        if (updated)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }
}
