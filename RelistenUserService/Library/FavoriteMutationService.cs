using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public sealed class FavoriteMutationService(
    AccountsDbContext dbContext,
    AdvisoryLockService advisoryLocks,
    CatalogAvailabilityValidator catalogValidator,
    LibraryStateStore stateStore,
    TimeProvider timeProvider)
{
    public const int MaximumActiveFavorites = 10_000;

    public async Task<FavoriteMutationExecution> ExecuteAsync(
        Guid userId,
        IReadOnlyList<FavoriteMutationCommand> mutations,
        CancellationToken cancellationToken)
    {
        var hashes = mutations.ToDictionary(
            mutation => mutation.MutationUuid,
            FavoriteMutationHasher.Hash);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        await LockCommandIdentitiesAsync(mutations, cancellationToken);

        var receiptById = await dbContext.FavoriteMutationReceipts
            .Where(receipt => hashes.Keys.Contains(receipt.Id))
            .ToDictionaryAsync(receipt => receipt.Id, cancellationToken);
        var conflictingMutations = mutations
            .Where(mutation => receiptById.TryGetValue(mutation.MutationUuid, out var receipt)
                && (receipt.UserId != userId
                    || !CryptographicOperations.FixedTimeEquals(
                        receipt.PayloadHash,
                        hashes[mutation.MutationUuid])))
            .Select(mutation => mutation.MutationUuid)
            .ToArray();
        if (conflictingMutations.Length > 0)
        {
            return FavoriteMutationExecution.Failed(new(
                FavoriteMutationFailureKind.IdempotencyConflict,
                "A mutation_uuid was already used by another account or semantic mutation.",
                conflictingMutations));
        }

        if (receiptById.Count == mutations.Count)
        {
            var storedResults = mutations
                .Select(mutation => ToResult(receiptById[mutation.MutationUuid]))
                .ToArray();
            await transaction.CommitAsync(cancellationToken);
            return FavoriteMutationExecution.Succeeded(new(
                FavoriteMutationRequestValidator.ContractVersion,
                storedResults.Max(result => result.LibraryRevision),
                storedResults));
        }

        var newMutations = mutations
            .Where(mutation => !receiptById.ContainsKey(mutation.MutationUuid))
            .ToArray();
        var favoriteIdentityFailure = await FindFavoriteIdentityConflictAsync(
            userId,
            newMutations,
            cancellationToken);
        if (favoriteIdentityFailure is not null)
        {
            return FavoriteMutationExecution.Failed(favoriteIdentityFailure);
        }

        var now = timeProvider.GetUtcNow();
        var state = await stateStore.GetOrCreateAsync(
            userId,
            now,
            LibraryStateLockMode.ExclusiveWrite,
            cancellationToken);
        var favorites = await dbContext.Favorites
            .Where(favorite => favorite.UserId == userId)
            .ToListAsync(cancellationToken);
        var simulation = SimulateNewState(favorites.Select(ToReference), newMutations);
        if (simulation.FinalCount > MaximumActiveFavorites)
        {
            return FavoriteMutationExecution.Failed(new(
                FavoriteMutationFailureKind.QuotaExceeded,
                $"An account may have at most {MaximumActiveFavorites:N0} active favorites."));
        }

        var unavailable = await catalogValidator.FindUnavailableAsync(
            simulation.AdditionsToValidate,
            cancellationToken);
        if (unavailable.Count > 0)
        {
            return FavoriteMutationExecution.Failed(new(
                FavoriteMutationFailureKind.CatalogUnavailable,
                "One or more new favorites are unavailable in the catalog.",
                UnavailableReferences: unavailable));
        }

        var activeByTarget = favorites.ToDictionary(ToReference);
        var knownById = favorites.ToDictionary(favorite => favorite.Id);
        var applier = new FavoriteMutationApplier(dbContext);
        var results = new List<FavoriteMutationResult>(mutations.Count);
        foreach (var mutation in mutations)
        {
            if (receiptById.TryGetValue(mutation.MutationUuid, out var storedReceipt))
            {
                results.Add(ToResult(storedReceipt));
                continue;
            }

            var result = applier.Apply(
                userId,
                mutation,
                state,
                activeByTarget,
                knownById,
                now);
            dbContext.FavoriteMutationReceipts.Add(new()
            {
                Id = mutation.MutationUuid,
                UserId = userId,
                CatalogType = mutation.CatalogType,
                CatalogUuid = mutation.CatalogUuid,
                DesiredState = mutation.DesiredState,
                SubmittedFavoriteId = mutation.FavoriteUuid,
                PayloadHash = hashes[mutation.MutationUuid],
                Changed = result.Changed,
                CanonicalFavoriteId = result.CanonicalFavoriteUuid,
                LibraryRevision = result.LibraryRevision,
                CreatedAt = now
            });
            results.Add(result);
        }

        state.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FavoriteMutationExecution.Succeeded(new(
            FavoriteMutationRequestValidator.ContractVersion,
            state.Revision,
            results));
    }

    private Task LockCommandIdentitiesAsync(
        IReadOnlyList<FavoriteMutationCommand> mutations,
        CancellationToken cancellationToken) =>
        advisoryLocks.LockAllAsync(mutations
            .Select(mutation => $"favorite-mutation:{mutation.MutationUuid:D}")
            .Concat(mutations
                .Where(IsAdd)
                .Select(mutation => $"favorite-row:{mutation.FavoriteUuid!.Value:D}"))
            .Distinct(StringComparer.Ordinal), cancellationToken);

    private async Task<FavoriteMutationFailure?> FindFavoriteIdentityConflictAsync(
        Guid userId,
        IReadOnlyList<FavoriteMutationCommand> mutations,
        CancellationToken cancellationToken)
    {
        var expectedById = new Dictionary<Guid, CatalogReference>();
        foreach (var mutation in mutations.Where(IsAdd))
        {
            var favoriteId = mutation.FavoriteUuid!.Value;
            var target = ToReference(mutation);
            if (expectedById.TryGetValue(favoriteId, out var expected) && expected != target)
            {
                return FavoriteUuidConflict([favoriteId]);
            }

            expectedById[favoriteId] = target;
        }

        if (expectedById.Count == 0)
        {
            return null;
        }

        var submittedFavoriteIds = expectedById.Keys.ToArray();

        var favoriteRows = await dbContext.Favorites
            .Where(favorite => submittedFavoriteIds.Contains(favorite.Id))
            .Select(favorite => new FavoriteIdentity(
                favorite.Id,
                favorite.UserId,
                favorite.CatalogType,
                favorite.CatalogUuid))
            .ToArrayAsync(cancellationToken);
        var changeRows = await dbContext.LibraryChanges
            .Where(change => submittedFavoriteIds.Contains(change.FavoriteId))
            .Select(change => new FavoriteIdentity(
                change.FavoriteId,
                change.UserId,
                change.CatalogType,
                change.CatalogUuid))
            .ToArrayAsync(cancellationToken);
        var receiptRows = await dbContext.FavoriteMutationReceipts
            .Where(receipt => receipt.SubmittedFavoriteId != null
                && submittedFavoriteIds.Contains(receipt.SubmittedFavoriteId.Value))
            .Select(receipt => new FavoriteIdentity(
                receipt.SubmittedFavoriteId!.Value,
                receipt.UserId,
                receipt.CatalogType,
                receipt.CatalogUuid))
            .ToArrayAsync(cancellationToken);

        var conflicts = favoriteRows
            .Concat(changeRows)
            .Concat(receiptRows)
            .Where(identity => identity.UserId != userId
                || !expectedById.TryGetValue(identity.FavoriteId, out var expected)
                || expected != new CatalogReference(identity.CatalogType, identity.CatalogUuid))
            .Select(identity => identity.FavoriteId)
            .Distinct()
            .Order()
            .ToArray();
        return conflicts.Length == 0 ? null : FavoriteUuidConflict(conflicts);
    }

    private static FavoriteStateSimulation SimulateNewState(
        IEnumerable<CatalogReference> initialTargets,
        IReadOnlyList<FavoriteMutationCommand> mutations)
    {
        var currentTargets = initialTargets.ToHashSet();
        var seenAdditions = new HashSet<CatalogReference>();
        var additions = new List<CatalogReference>();
        foreach (var mutation in mutations)
        {
            var target = ToReference(mutation);
            if (IsAdd(mutation))
            {
                if (currentTargets.Add(target) && seenAdditions.Add(target))
                {
                    additions.Add(target);
                }
            }
            else
            {
                currentTargets.Remove(target);
            }
        }

        return new(currentTargets.Count, additions);
    }

    private static FavoriteMutationResult ToResult(FavoriteMutationReceipt receipt) => new(
        receipt.Id,
        receipt.CatalogType,
        receipt.CatalogUuid,
        receipt.DesiredState,
        receipt.Changed,
        receipt.SubmittedFavoriteId,
        receipt.CanonicalFavoriteId,
        receipt.LibraryRevision);

    private static CatalogReference ToReference(Favorite favorite) =>
        new(favorite.CatalogType, favorite.CatalogUuid);

    private static CatalogReference ToReference(FavoriteMutationCommand mutation) =>
        new(mutation.CatalogType, mutation.CatalogUuid);

    private static bool IsAdd(FavoriteMutationCommand mutation) =>
        mutation.DesiredState == FavoriteDesiredStates.Favorite;

    private static FavoriteMutationFailure FavoriteUuidConflict(IReadOnlyList<Guid> ids) => new(
        FavoriteMutationFailureKind.FavoriteUuidConflict,
        "A favorite_uuid was already assigned to another account or catalog target.",
        ids);

    private sealed record FavoriteIdentity(
        Guid FavoriteId,
        Guid UserId,
        string CatalogType,
        Guid CatalogUuid);

    private sealed record FavoriteStateSimulation(
        int FinalCount,
        IReadOnlyList<CatalogReference> AdditionsToValidate);
}
