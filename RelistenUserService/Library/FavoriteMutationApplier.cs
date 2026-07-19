using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

// Applies one already-validated command to EF's tracked membership graph. Transaction boundaries,
// idempotency, catalog checks, and account-level serialization remain in FavoriteMutationService.
internal sealed class FavoriteMutationApplier(AccountsDbContext dbContext)
{
    public FavoriteMutationResult Apply(
        Guid userId,
        FavoriteMutationCommand mutation,
        LibraryState state,
        IDictionary<CatalogReference, Favorite> activeByTarget,
        IDictionary<Guid, Favorite> knownById,
        DateTimeOffset now)
    {
        var target = new CatalogReference(mutation.CatalogType, mutation.CatalogUuid);
        var changed = false;
        Guid? canonicalFavoriteId;
        if (mutation.DesiredState == FavoriteDesiredStates.Favorite)
        {
            if (activeByTarget.TryGetValue(target, out var existing))
            {
                canonicalFavoriteId = existing.Id;
            }
            else
            {
                var favorite = RestoreOrCreateFavorite(
                    mutation.FavoriteUuid!.Value,
                    userId,
                    target,
                    knownById,
                    now);
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
            mutation.MutationUuid,
            mutation.CatalogType,
            mutation.CatalogUuid,
            mutation.DesiredState,
            changed,
            mutation.FavoriteUuid,
            canonicalFavoriteId,
            state.Revision);
    }

    private Favorite RestoreOrCreateFavorite(
        Guid favoriteId,
        Guid userId,
        CatalogReference target,
        IDictionary<Guid, Favorite> knownById,
        DateTimeOffset now)
    {
        if (!knownById.TryGetValue(favoriteId, out var favorite))
        {
            favorite = new()
            {
                Id = favoriteId,
                UserId = userId,
                CatalogType = target.CatalogType,
                CatalogUuid = target.CatalogUuid
            };
            knownById.Add(favoriteId, favorite);
        }

        favorite.CreatedAt = now;
        favorite.UpdatedAt = now;
        var entry = dbContext.Entry(favorite);
        entry.State = entry.State == EntityState.Deleted
            ? EntityState.Modified
            : EntityState.Added;
        return favorite;
    }

    private void AppendChange(
        LibraryState state,
        Favorite favorite,
        string changeType,
        DateTimeOffset now)
    {
        state.Revision++;
        dbContext.LibraryChanges.Add(new()
        {
            UserId = favorite.UserId,
            Revision = state.Revision,
            ChangeType = changeType,
            FavoriteId = favorite.Id,
            CatalogType = favorite.CatalogType,
            CatalogUuid = favorite.CatalogUuid,
            ChangedAt = now
        });
    }
}
