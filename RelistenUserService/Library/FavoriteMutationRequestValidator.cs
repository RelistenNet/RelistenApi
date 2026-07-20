using Relisten.Accounts.Contracts.Errors;
using Relisten.Accounts.Contracts.Library;

namespace RelistenUserService.Library;

public sealed record FavoriteMutationValidationFailure(string Code, string Detail);

public sealed record FavoriteMutationCommand(
    Guid MutationUuid,
    string CatalogType,
    Guid CatalogUuid,
    string DesiredState,
    Guid? FavoriteUuid);

public static class FavoriteMutationRequestValidator
{
    public const int ContractVersion = 1;
    public const int MaximumBatchSize = 500;

    public static bool TryValidate(
        FavoriteMutationBatchRequest? request,
        out IReadOnlyList<FavoriteMutationCommand> mutations,
        out FavoriteMutationValidationFailure? failure)
    {
        mutations = [];
        if (request is null)
        {
            failure = Invalid("A JSON request body is required.");
            return false;
        }

        if (request.ContractVersion != ContractVersion)
        {
            failure = new(
                AccountErrorCodes.InvalidContractVersion,
                $"contract_version must be {ContractVersion}.");
            return false;
        }

        if (request.Mutations is not { Count: > 0 })
        {
            failure = Invalid("mutations must contain at least one item.");
            return false;
        }

        if (request.Mutations.Count > MaximumBatchSize)
        {
            failure = new(
                LibraryErrorCodes.LimitExceeded,
                $"mutations may contain at most {MaximumBatchSize} items.");
            return false;
        }

        var validated = new List<FavoriteMutationCommand>(request.Mutations.Count);
        var mutationIds = new HashSet<Guid>();
        for (var index = 0; index < request.Mutations.Count; index++)
        {
            var candidate = request.Mutations[index];
            if (candidate is null)
            {
                failure = InvalidAt(index, "must be an object");
                return false;
            }

            if (!IsUuidV7(candidate.MutationUuid))
            {
                failure = InvalidAt(index, "mutation_uuid must be a UUIDv7 value");
                return false;
            }

            if (!mutationIds.Add(candidate.MutationUuid))
            {
                failure = InvalidAt(index, "mutation_uuid is duplicated in this batch");
                return false;
            }

            if (!FavoriteCatalogTypes.IsSupported(candidate.CatalogType))
            {
                failure = InvalidAt(
                    index,
                    "catalog_type must be one of artist, show, source, source_track, song, tour, or venue");
                return false;
            }

            if (candidate.CatalogUuid == Guid.Empty)
            {
                failure = InvalidAt(index, "catalog_uuid must be a non-empty UUID");
                return false;
            }

            if (!FavoriteDesiredStates.IsSupported(candidate.DesiredState))
            {
                failure = InvalidAt(
                    index,
                    "desired_state must be favorite or not_favorite");
                return false;
            }

            if (candidate.DesiredState == FavoriteDesiredStates.Favorite)
            {
                if (candidate.FavoriteUuid is not { } favoriteUuid || !IsUuidV7(favoriteUuid))
                {
                    failure = InvalidAt(
                        index,
                        "favorite_uuid must be a UUIDv7 value when desired_state is favorite");
                    return false;
                }
            }
            else if (candidate.FavoriteUuid is not null)
            {
                failure = InvalidAt(
                    index,
                    "favorite_uuid must be omitted when desired_state is not_favorite");
                return false;
            }

            validated.Add(new(
                candidate.MutationUuid,
                candidate.CatalogType!,
                candidate.CatalogUuid,
                candidate.DesiredState!,
                candidate.FavoriteUuid));
        }

        mutations = validated;
        failure = null;
        return true;
    }

    private static bool IsUuidV7(Guid value) => value != Guid.Empty && value.Version == 7;

    private static FavoriteMutationValidationFailure Invalid(string detail) =>
        new(LibraryErrorCodes.InvalidFavoriteMutation, detail);

    private static FavoriteMutationValidationFailure InvalidAt(int index, string detail) =>
        Invalid($"mutations[{index}] {detail}.");
}
