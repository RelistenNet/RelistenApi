using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

// Applies one already-validated command to EF's tracked membership graph. Transaction boundaries,
// idempotency, quota enforcement, and account-level serialization remain in FavoriteMutationService.
internal sealed class FavoriteMutationApplier(AccountsDbContext dbContext)
{
    public FavoriteMutationResult Apply(
        UserId userId,
        FavoriteMutationCommand mutation,
        LibraryState state,
        IDictionary<CatalogReference, Favorite> activeByTarget,
        IDictionary<FavoriteId, Favorite> knownById,
        DateTimeOffset now
    )
    {
        var target = new CatalogReference(mutation.CatalogType, mutation.CatalogId);
        var changed = false;
        FavoriteId? canonicalFavoriteId;
        if (mutation.DesiredState == FavoriteDesiredStates.Favorite)
        {
            if (activeByTarget.TryGetValue(target, out var existing))
            {
                canonicalFavoriteId = existing.Id;
            }
            else
            {
                var favorite = RestoreOrCreateFavorite(
                    mutation.FavoriteId!.Value,
                    userId,
                    target,
                    knownById,
                    now
                );
                activeByTarget[target] = favorite;
                canonicalFavoriteId = favorite.Id;
                changed = true;
                AppendChange(state, favorite, FavoriteChangeTypes.Added, now);
            }
        }
        else if (activeByTarget.Remove(target, out var removed))
        {
            canonicalFavoriteId = removed.Id;
            changed = true;
            dbContext.Favorites.Remove(removed);
            AppendChange(state, removed, FavoriteChangeTypes.Removed, now);
        }
        else
        {
            canonicalFavoriteId = null;
        }

        return new(
            mutation.MutationId,
            new FavoriteTarget(mutation.CatalogType, mutation.CatalogId),
            mutation.DesiredState,
            changed,
            mutation.FavoriteId,
            canonicalFavoriteId,
            state.Revision
        );
    }

    private Favorite RestoreOrCreateFavorite(
        FavoriteId favoriteId,
        UserId userId,
        CatalogReference target,
        IDictionary<FavoriteId, Favorite> knownById,
        DateTimeOffset now
    )
    {
        if (!knownById.TryGetValue(favoriteId, out var favorite))
        {
            favorite = new()
            {
                Id = favoriteId,
                UserId = userId,
                CatalogType = target.CatalogType,
                CatalogId = target.CatalogId,
            };
            knownById.Add(favoriteId, favorite);
        }

        favorite.CreatedAt = now;
        favorite.UpdatedAt = now;
        var entry = dbContext.Entry(favorite);
        entry.State = entry.State == EntityState.Deleted ? EntityState.Modified : EntityState.Added;
        return favorite;
    }

    private void AppendChange(
        LibraryState state,
        Favorite favorite,
        string changeType,
        DateTimeOffset now
    )
    {
        state.Revision++;
        dbContext.LibraryChanges.Add(
            new()
            {
                UserId = favorite.UserId,
                Revision = state.Revision,
                ChangeType = changeType,
                FavoriteId = favorite.Id,
                CatalogType = favorite.CatalogType,
                CatalogId = favorite.CatalogId,
                ChangedAt = now,
            }
        );
    }
}
