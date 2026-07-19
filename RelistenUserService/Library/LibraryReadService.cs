using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public sealed record LibraryChangesExecution(
    FavoriteLibraryChanges? Response,
    bool CursorExpired)
{
    public static LibraryChangesExecution Succeeded(FavoriteLibraryChanges response) =>
        new(response, false);

    public static LibraryChangesExecution Expired() => new(null, true);
}

public sealed class LibraryReadService(
    AccountsDbContext dbContext,
    LibraryStateStore stateStore,
    LibraryCursorProtector cursorProtector,
    TimeProvider timeProvider)
{
    public const int MaximumChangesPerPage = 500;

    public async Task<FavoriteLibrarySnapshot> GetSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var state = await stateStore.GetOrCreateAsync(
            userId,
            timeProvider.GetUtcNow(),
            LibraryStateLockMode.SharedRead,
            cancellationToken);
        var favorites = await dbContext.Favorites
            .AsNoTracking()
            .Where(favorite => favorite.UserId == userId)
            .OrderBy(favorite => favorite.CreatedAt)
            .ThenBy(favorite => favorite.Id)
            .Select(favorite => new FavoriteSnapshotItem(
                favorite.Id,
                favorite.CatalogType,
                favorite.CatalogUuid,
                favorite.CreatedAt,
                favorite.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            FavoriteMutationRequestValidator.ContractVersion,
            state.Revision,
            cursorProtector.Protect(userId, state.Revision),
            favorites);
    }

    public async Task<LibraryChangesExecution> GetChangesAsync(
        Guid userId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!cursorProtector.TryUnprotect(cursor, userId, out var afterRevision))
        {
            return LibraryChangesExecution.Expired();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var state = await stateStore.GetOrCreateAsync(
            userId,
            timeProvider.GetUtcNow(),
            LibraryStateLockMode.SharedRead,
            cancellationToken);
        if (afterRevision > state.Revision)
        {
            return LibraryChangesExecution.Expired();
        }

        var changes = await dbContext.LibraryChanges
            .AsNoTracking()
            .Where(change => change.UserId == userId && change.Revision > afterRevision)
            .OrderBy(change => change.Revision)
            .Take(MaximumChangesPerPage + 1)
            .ToArrayAsync(cancellationToken);

        // Revisions are contiguous per user. A gap means the cursor predates retained history,
        // even if the protected token itself is still cryptographically valid.
        if (afterRevision < state.Revision
            && (changes.Length == 0 || changes[0].Revision != afterRevision + 1))
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
                    change.CatalogType,
                    change.CatalogUuid,
                    change.ChangedAt))
                .ToArray(),
            cursorProtector.Protect(userId, nextRevision),
            hasMore);
        await transaction.CommitAsync(cancellationToken);
        return LibraryChangesExecution.Succeeded(response);
    }
}
