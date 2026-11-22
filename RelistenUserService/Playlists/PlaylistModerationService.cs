using Microsoft.EntityFrameworkCore;
using RelistenUserService.Library;
using RelistenUserService.Persistence;
using RelistenUserService.Search;

namespace RelistenUserService.Playlists;

/// <summary>Operator moderation of any existing playlist, run from the moderation command.</summary>
internal sealed class PlaylistModerationService(
    AccountsDbContext database,
    LibraryStateStore stateStore,
    TimeProvider clock,
    PlaylistSearchNotifier? search = null
)
{
    internal async Task<bool> SetTakedownAsync(
        PlaylistId id,
        bool takenDown,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        var owner = await database
            .Playlists.Where(row => row.Id == id)
            .Select(row => (UserId?)row.OwnerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            throw PlaylistVisibility.NotFound();
        }

        // Serialize with owner edits and share commands. The operator can moderate
        // any existing playlist, including one owned by a disabled account.
        if (
            !await PlaylistAccountLock.AcquireAsync(
                database,
                owner.Value,
                requireActive: false,
                cancellationToken
            )
        )
        {
            throw PlaylistVisibility.NotFound();
        }

        var now = clock.GetUtcNow();
        var state = await stateStore.GetOrCreateForUpdateAsync(owner.Value, now, cancellationToken);
        var playlist = await PlaylistWriteService.LockOwnedPlaylistAsync(
            database,
            owner.Value,
            id,
            cancellationToken
        );
        if (playlist.TakenDown == takenDown)
        {
            return false;
        }

        // The owner's devices still learn the new state through a summary, but an operator
        // action leaves updated_at alone so it does not reorder the owner's playlist list.
        playlist.TakenDown = takenDown;
        stateStore.Append(state, PlaylistWriteService.SummaryChange(playlist, now));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (search is not null)
        {
            await search.Changed(id);
        }
        return true;
    }
}
