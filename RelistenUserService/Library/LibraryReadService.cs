using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using Relisten.Accounts.Contracts.Playlists;
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
        var playlists = await dbContext
            .Playlists.AsNoTracking()
            .Where(row => row.OwnerId == userId)
            .OrderByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.Id)
            .Select(row => new PlaylistSummary(
                row.Id,
                row.Name,
                row.EntryCount,
                row.Archived,
                row.Revision,
                row.UpdatedAt
            ))
            .ToArrayAsync(cancellationToken);
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

        if (afterRevision == head.Revision)
        {
            await transaction.CommitAsync(cancellationToken);
            return LibraryChangesExecution.Succeeded(
                new(
                    FavoriteMutationRequestValidator.ContractVersion,
                    head.Revision,
                    [],
                    cursorProtector.Protect(userId, head.Revision, head.Generation),
                    false
                )
                {
                    HistoryGeneration = head.Generation,
                }
            );
        }

        var changes = await dbContext
            .LibraryChanges.AsNoTracking()
            .Where(change => change.UserId == userId && change.Revision > afterRevision)
            .OrderBy(change => change.Revision)
            .Take(MaximumChangesPerPage + 1)
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Revisions are contiguous per user. A gap means the cursor predates retained history,
        // even if the protected token itself is still cryptographically valid.
        if (changes.Length == 0 || changes[0].Revision != afterRevision + 1)
        {
            return LibraryChangesExecution.Expired();
        }

        var hasMore = changes.Length > MaximumChangesPerPage;
        var page = changes.Take(MaximumChangesPerPage).ToArray();
        var nextRevision = hasMore ? page[^1].Revision : head.Revision;
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
                    change.PlaylistJson is null
                        ? null
                        : JsonSerializer.Deserialize<PlaylistSummary>(
                            change.PlaylistJson,
                            PlaylistJson.Options
                        )
                ))
                .ToArray(),
            cursorProtector.Protect(userId, nextRevision, head.Generation),
            hasMore
        )
        {
            HistoryGeneration = head.Generation,
        };
        return LibraryChangesExecution.Succeeded(response);
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
                    ?? 0
            ))
            .SingleAsync(cancellationToken);

    private sealed record LibraryHead(string Generation, long Revision);
}
