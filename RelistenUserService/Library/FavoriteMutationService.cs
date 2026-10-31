using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public sealed class FavoriteMutationService(
    AccountsDbContext dbContext,
    AdvisoryLockService advisoryLocks,
    LibraryStateStore stateStore,
    TimeProvider timeProvider
)
{
    public const int MaximumActiveFavorites = 10_000;

    public async Task<FavoriteMutationExecution> ExecuteAsync(
        UserId userId,
        IReadOnlyList<FavoriteMutationCommand> mutations,
        CancellationToken cancellationToken
    )
    {
        var hashes = mutations.ToDictionary(
            mutation => mutation.MutationId,
            FavoriteMutationHasher.Hash
        );
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        await LockCommandIdentitiesAsync(mutations, cancellationToken);

        var receiptById = await dbContext
            .FavoriteMutationReceipts.Where(receipt => hashes.Keys.Contains(receipt.Id))
            .ToDictionaryAsync(receipt => receipt.Id, cancellationToken);
        var conflictingMutations = mutations
            .Where(mutation =>
                receiptById.TryGetValue(mutation.MutationId, out var receipt)
                && (
                    receipt.UserId != userId
                    || !CryptographicOperations.FixedTimeEquals(
                        receipt.PayloadHash,
                        hashes[mutation.MutationId]
                    )
                )
            )
            .Select(mutation => mutation.MutationId)
            .ToArray();
        if (conflictingMutations.Length > 0)
        {
            return FavoriteMutationExecution.Failed(
                new(
                    FavoriteMutationFailureKind.IdempotencyConflict,
                    "A mutation_id was already used by another account or semantic mutation.",
                    conflictingMutations.Select(id => id.Value).ToArray()
                )
            );
        }

        if (receiptById.Count == mutations.Count)
        {
            var storedResults = mutations
                .Select(mutation => ToResult(receiptById[mutation.MutationId]))
                .ToArray();
            await transaction.CommitAsync(cancellationToken);
            return FavoriteMutationExecution.Succeeded(
                new(
                    FavoriteMutationRequestValidator.ContractVersion,
                    storedResults.Max(result => result.LibraryRevision),
                    storedResults
                )
            );
        }

        var newMutations = mutations
            .Where(mutation => !receiptById.ContainsKey(mutation.MutationId))
            .ToArray();
        var favoriteIdentityFailure = await FindFavoriteIdentityConflictAsync(
            userId,
            newMutations,
            cancellationToken
        );
        if (favoriteIdentityFailure is not null)
        {
            return FavoriteMutationExecution.Failed(favoriteIdentityFailure);
        }

        var now = timeProvider.GetUtcNow();
        var state = await stateStore.GetOrCreateAsync(
            userId,
            now,
            LibraryStateLockMode.ExclusiveWrite,
            cancellationToken
        );
        var favorites = await dbContext
            .Favorites.Where(favorite => favorite.UserId == userId)
            .ToListAsync(cancellationToken);
        var finalCount = CountFavoritesAfterMutations(favorites.Select(ToReference), newMutations);
        if (finalCount > MaximumActiveFavorites)
        {
            return FavoriteMutationExecution.Failed(
                new(
                    FavoriteMutationFailureKind.QuotaExceeded,
                    $"An account may have at most {MaximumActiveFavorites:N0} active favorites."
                )
            );
        }

        var activeByTarget = favorites.ToDictionary(ToReference);
        var knownById = favorites.ToDictionary(favorite => favorite.Id);
        var applier = new FavoriteMutationApplier(dbContext);
        var results = new List<FavoriteMutationResult>(mutations.Count);
        foreach (var mutation in mutations)
        {
            if (receiptById.TryGetValue(mutation.MutationId, out var storedReceipt))
            {
                results.Add(ToResult(storedReceipt));
                continue;
            }

            var result = applier.Apply(userId, mutation, state, activeByTarget, knownById, now);
            dbContext.FavoriteMutationReceipts.Add(
                new()
                {
                    Id = mutation.MutationId,
                    UserId = userId,
                    CatalogType = mutation.CatalogType,
                    CatalogId = mutation.CatalogId,
                    DesiredState = mutation.DesiredState,
                    SubmittedFavoriteId = mutation.FavoriteId,
                    PayloadHash = hashes[mutation.MutationId],
                    Changed = result.Changed,
                    CanonicalFavoriteId = result.CanonicalFavoriteId,
                    LibraryRevision = result.LibraryRevision,
                    CreatedAt = now,
                }
            );
            results.Add(result);
        }

        state.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FavoriteMutationExecution.Succeeded(
            new(FavoriteMutationRequestValidator.ContractVersion, state.Revision, results)
        );
    }

    private Task LockCommandIdentitiesAsync(
        IReadOnlyList<FavoriteMutationCommand> mutations,
        CancellationToken cancellationToken
    ) =>
        advisoryLocks.LockAllAsync(
            mutations
                .Select(mutation => $"favorite-mutation:{mutation.MutationId}")
                .Concat(
                    mutations
                        .Where(IsAdd)
                        .Select(mutation => $"favorite-row:{mutation.FavoriteId!.Value}")
                )
                .Distinct(StringComparer.Ordinal),
            cancellationToken
        );

    private async Task<FavoriteMutationFailure?> FindFavoriteIdentityConflictAsync(
        UserId userId,
        IReadOnlyList<FavoriteMutationCommand> mutations,
        CancellationToken cancellationToken
    )
    {
        var expectedById = new Dictionary<FavoriteId, CatalogReference>();
        foreach (var mutation in mutations.Where(IsAdd))
        {
            var favoriteId = mutation.FavoriteId!.Value;
            var target = ToReference(mutation);
            if (expectedById.TryGetValue(favoriteId, out var expected) && expected != target)
            {
                return FavoriteIdConflict([favoriteId]);
            }

            expectedById[favoriteId] = target;
        }

        if (expectedById.Count == 0)
        {
            return null;
        }

        var submittedFavoriteIds = expectedById.Keys.ToArray();

        var favoriteRows = await dbContext
            .Favorites.Where(favorite => submittedFavoriteIds.Contains(favorite.Id))
            .Select(favorite => new FavoriteIdentity(
                favorite.Id,
                favorite.UserId,
                favorite.CatalogType,
                favorite.CatalogId
            ))
            .ToArrayAsync(cancellationToken);
        var changeRows = await dbContext
            .LibraryChanges.Where(change => submittedFavoriteIds.Contains(change.FavoriteId))
            .Select(change => new FavoriteIdentity(
                change.FavoriteId,
                change.UserId,
                change.CatalogType,
                change.CatalogId
            ))
            .ToArrayAsync(cancellationToken);
        var receiptRows = await dbContext
            .FavoriteMutationReceipts.Where(receipt =>
                receipt.SubmittedFavoriteId != null
                && submittedFavoriteIds.Contains(receipt.SubmittedFavoriteId.Value)
            )
            .Select(receipt => new FavoriteIdentity(
                receipt.SubmittedFavoriteId!.Value,
                receipt.UserId,
                receipt.CatalogType,
                receipt.CatalogId
            ))
            .ToArrayAsync(cancellationToken);

        var conflicts = favoriteRows
            .Concat(changeRows)
            .Concat(receiptRows)
            .Where(identity =>
                identity.UserId != userId
                || !expectedById.TryGetValue(identity.FavoriteId, out var expected)
                || expected != new CatalogReference(identity.CatalogType, identity.CatalogId)
            )
            .Select(identity => identity.FavoriteId)
            .Distinct()
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
        return conflicts.Length == 0 ? null : FavoriteIdConflict(conflicts);
    }

    private static int CountFavoritesAfterMutations(
        IEnumerable<CatalogReference> initialTargets,
        IReadOnlyList<FavoriteMutationCommand> mutations
    )
    {
        var currentTargets = initialTargets.ToHashSet();
        foreach (var mutation in mutations)
        {
            var target = ToReference(mutation);
            if (IsAdd(mutation))
            {
                currentTargets.Add(target);
            }
            else
            {
                currentTargets.Remove(target);
            }
        }

        return currentTargets.Count;
    }

    private static FavoriteMutationResult ToResult(FavoriteMutationReceipt receipt) =>
        new(
            receipt.Id,
            new FavoriteTarget(receipt.CatalogType, receipt.CatalogId),
            receipt.DesiredState,
            receipt.Changed,
            receipt.SubmittedFavoriteId,
            receipt.CanonicalFavoriteId,
            receipt.LibraryRevision
        );

    private static CatalogReference ToReference(Favorite favorite) =>
        new(favorite.CatalogType, favorite.CatalogId);

    private static CatalogReference ToReference(FavoriteMutationCommand mutation) =>
        new(mutation.CatalogType, mutation.CatalogId);

    private static bool IsAdd(FavoriteMutationCommand mutation) =>
        mutation.DesiredState == FavoriteDesiredStates.Favorite;

    private static FavoriteMutationFailure FavoriteIdConflict(IReadOnlyList<FavoriteId> ids) =>
        new(
            FavoriteMutationFailureKind.FavoriteIdConflict,
            "A favorite_id was already assigned to another account or catalog target.",
            ids.Select(id => id.Value).ToArray()
        );

    private sealed record FavoriteIdentity(
        FavoriteId FavoriteId,
        UserId UserId,
        string CatalogType,
        string CatalogId
    );
}
