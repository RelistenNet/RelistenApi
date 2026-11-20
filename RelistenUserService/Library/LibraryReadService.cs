using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;

namespace RelistenUserService.Library;

public sealed record LibraryChangesExecution(FavoriteLibraryChanges? Response, bool CursorExpired)
{
    public static LibraryChangesExecution Succeeded(FavoriteLibraryChanges response) =>
        new(response, false);

    public static LibraryChangesExecution Expired() => new(null, true);
}

public sealed class LibraryReadService(
    AccountsDbContext dbContext,
    LibraryCursorProtector cursorProtector
)
{
    public const int MaximumChangesPerPage = 500;

    public async Task<FavoriteLibrarySnapshot> GetSnapshotAsync(
        UserId userId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var head = await ReadHeadAsync(userId, cancellationToken);
        var favorites = await dbContext
            .Favorites.AsNoTracking()
            .Where(favorite => favorite.UserId == userId)
            .OrderBy(favorite => favorite.CreatedAt)
            .ThenBy(favorite => favorite.Id)
            .Select(favorite => new FavoriteSnapshotItem(
                favorite.Id,
                new FavoriteTarget(favorite.CatalogType, favorite.CatalogId),
                favorite.CreatedAt,
                favorite.UpdatedAt
            ))
            .ToArrayAsync(cancellationToken);
        var ownedPlaylists = dbContext
            .Playlists.AsNoTracking()
            .Where(row => row.OwnerId == userId)
            .OrderByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.Id);
        var playlists = await PlaylistReadService
            .Summaries(ownedPlaylists)
            .ToArrayAsync(cancellationToken);
        var followedPlaylists = await ReadFollowedHeadsAsync(head, userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            FavoriteMutationRequestValidator.ContractVersion,
            head.Revision,
            cursorProtector.Protect(userId, head.Revision, head.Generation),
            favorites
        )
        {
            HistoryGeneration = head.Generation,
            Playlists = playlists,
            FollowedPlaylists = followedPlaylists,
        };
    }

    public async Task<LibraryChangesExecution> GetChangesAsync(
        UserId userId,
        string? cursor,
        CancellationToken cancellationToken
    )
    {
        if (
            !cursorProtector.TryUnprotect(
                cursor,
                userId,
                out var afterRevision,
                out var cursorGeneration
            )
        )
        {
            return LibraryChangesExecution.Expired();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var head = await ReadHeadAsync(userId, cancellationToken);
        if (afterRevision > head.Revision || head.Generation != cursorGeneration)
        {
            return LibraryChangesExecution.Expired();
        }

        var changes =
            afterRevision == head.Revision
                ? []
                : await dbContext
                    .LibraryChanges.AsNoTracking()
                    .Where(change => change.UserId == userId && change.Revision > afterRevision)
                    .OrderBy(change => change.Revision)
                    .Take(MaximumChangesPerPage + 1)
                    .ToArrayAsync(cancellationToken);

        // Revisions are contiguous per user. A gap means the cursor predates retained history,
        // even if the protected token itself is still cryptographically valid.
        if (
            afterRevision != head.Revision
            && (changes.Length == 0 || changes[0].Revision != afterRevision + 1)
        )
        {
            return LibraryChangesExecution.Expired();
        }

        var hasMore = changes.Length > MaximumChangesPerPage;
        var page = changes.Take(MaximumChangesPerPage).ToArray();
        var nextRevision = hasMore ? page[^1].Revision : head.Revision;
        var followedPlaylists = hasMore
            ? null
            : await ReadFollowedHeadsAsync(head, userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var response = new FavoriteLibraryChanges(
            FavoriteMutationRequestValidator.ContractVersion,
            head.Revision,
            page.Select(change => new FavoriteLibraryChange(
                    change.Id,
                    change.Revision,
                    change.ChangeType,
                    change.FavoriteId,
                    change.FavoriteId is null
                        ? null
                        : new FavoriteTarget(change.CatalogType!, change.CatalogId!),
                    change.ChangedAt,
                    ReadPlaylistSummary(change),
                    change.FollowedPlaylistId
                ))
                .ToArray(),
            cursorProtector.Protect(userId, nextRevision, head.Generation),
            hasMore
        )
        {
            HistoryGeneration = head.Generation,
            FollowedPlaylists = followedPlaylists,
        };
        return LibraryChangesExecution.Succeeded(response);
    }

    private static PlaylistSummary? ReadPlaylistSummary(LibraryChange change)
    {
        if (change.PlaylistJson is null)
        {
            return null;
        }

        var summary = JsonSerializer.Deserialize<PlaylistSummary>(
            change.PlaylistJson,
            PlaylistJson.Options
        );
        return summary is null
            ? null
            : summary with
            {
                DurationSeconds = change.PlaylistDurationSeconds,
            };
    }

    private Task<LibraryHead> ReadHeadAsync(UserId userId, CancellationToken cancellationToken) =>
        // Writers commit the revision and library-visible data atomically. One repeatable-read
        // snapshot binds this head to the later rows without blocking those writers. Before the
        // first mutation, no state row is needed: the account's library is at revision zero.
        dbContext
            .HistoryGenerations.AsNoTracking()
            .Select(generation => new LibraryHead(
                generation.Value,
                dbContext
                    .LibraryStates.Where(state => state.UserId == userId)
                    .Select(state => (long?)state.Revision)
                    .SingleOrDefault()
                    ?? 0,
                dbContext.PlaylistFollows.Any(follow => follow.UserId == userId)
            ))
            .SingleAsync(cancellationToken);

    private Task<FollowedPlaylistHead[]> ReadFollowedHeadsAsync(
        LibraryHead head,
        UserId userId,
        CancellationToken cancellationToken
    ) =>
        head.HasFollows
            ? PlaylistReadService.FollowedHeadsAsync(dbContext, userId, cancellationToken)
            : Task.FromResult<FollowedPlaylistHead[]>([]);

    private sealed record LibraryHead(string Generation, long Revision, bool HasFollows);
}
