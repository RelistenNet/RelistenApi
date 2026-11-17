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
    LibraryStateStore stateStore,
    LibraryCursorProtector cursorProtector,
    TimeProvider timeProvider
)
{
    public const int MaximumChangesPerPage = 500;

    public async Task<FavoriteLibrarySnapshot> GetSnapshotAsync(
        UserId userId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await stateStore.GetOrCreateAsync(
            userId,
            timeProvider.GetUtcNow(),
            LibraryStateLockMode.SharedRead,
            cancellationToken
        );
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
        var generation = await dbContext
            .HistoryGenerations.Select(row => row.Value)
            .SingleAsync(cancellationToken);
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
            state.Revision,
            cursorProtector.Protect(userId, state.Revision, generation),
            favorites
        )
        {
            HistoryGeneration = generation,
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
            cancellationToken
        );
        var state = await stateStore.GetOrCreateAsync(
            userId,
            timeProvider.GetUtcNow(),
            LibraryStateLockMode.SharedRead,
            cancellationToken
        );
        var generation = await dbContext
            .HistoryGenerations.Select(row => row.Value)
            .SingleAsync(cancellationToken);
        if (afterRevision > state.Revision || generation != cursorGeneration)
        {
            return LibraryChangesExecution.Expired();
        }

        var changes = await dbContext
            .LibraryChanges.AsNoTracking()
            .Where(change => change.UserId == userId && change.Revision > afterRevision)
            .OrderBy(change => change.Revision)
            .Take(MaximumChangesPerPage + 1)
            .ToArrayAsync(cancellationToken);

        // Revisions are contiguous per user. A gap means the cursor predates retained history,
        // even if the protected token itself is still cryptographically valid.
        if (
            afterRevision < state.Revision
            && (changes.Length == 0 || changes[0].Revision != afterRevision + 1)
        )
        {
            return LibraryChangesExecution.Expired();
        }

        var hasMore = changes.Length > MaximumChangesPerPage;
        var page = changes.Take(MaximumChangesPerPage).ToArray();
        var nextRevision = hasMore ? page[^1].Revision : state.Revision;
        var response = new FavoriteLibraryChanges(
            FavoriteMutationRequestValidator.ContractVersion,
            state.Revision,
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
            cursorProtector.Protect(userId, nextRevision, generation),
            hasMore
        )
        {
            HistoryGeneration = generation,
        };
        await transaction.CommitAsync(cancellationToken);
        return LibraryChangesExecution.Succeeded(response);
    }
}
