using Relisten.Accounts.Contracts.Errors;
using Relisten.Accounts.Contracts.Library;

namespace RelistenUserService.Library;

public sealed record FavoriteMutationValidationFailure(string Code, string Detail);

public sealed record FavoriteMutationCommand(
    MutationId MutationId,
    string CatalogType,
    string CatalogId,
    string DesiredState,
    FavoriteId? FavoriteId
);

public static class FavoriteMutationRequestValidator
{
    public const int ContractVersion = 1;
    public const int MaximumBatchSize = 500;

    public static bool TryValidate(
        FavoriteMutationBatchRequest? request,
        out IReadOnlyList<FavoriteMutationCommand> mutations,
        out FavoriteMutationValidationFailure? failure
    )
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
                $"contract_version must be {ContractVersion}."
            );
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
                $"mutations may contain at most {MaximumBatchSize} items."
            );
            return false;
        }

        var validated = new List<FavoriteMutationCommand>(request.Mutations.Count);
        var mutationIds = new HashSet<MutationId>();
        for (var index = 0; index < request.Mutations.Count; index++)
        {
            var candidate = request.Mutations[index];
            if (candidate is null)
            {
                failure = InvalidAt(index, "must be an object");
                return false;
            }

            if (candidate.MutationId.IsEmpty)
            {
                failure = InvalidAt(index, "mutation_id must be a mutation_ UUIDv7 ID");
                return false;
            }

            if (!mutationIds.Add(candidate.MutationId))
            {
                failure = InvalidAt(index, "mutation_id is duplicated in this batch");
                return false;
            }

            if (candidate.Target is null || !candidate.Target.IsValid())
            {
                failure = InvalidAt(
                    index,
                    "target must contain a supported kind and matching typed id"
                );
                return false;
            }

            if (!FavoriteDesiredStates.IsSupported(candidate.DesiredState))
            {
                failure = InvalidAt(index, "desired_state must be favorite or not_favorite");
                return false;
            }

            if (candidate.DesiredState == FavoriteDesiredStates.Favorite)
            {
                if (candidate.FavoriteId is not { } favoriteId || favoriteId.IsEmpty)
                {
                    failure = InvalidAt(
                        index,
                        "favorite_id must be a favorite_ UUIDv7 ID when desired_state is favorite"
                    );
                    return false;
                }
            }
            else if (candidate.FavoriteId is not null)
            {
                failure = InvalidAt(
                    index,
                    "favorite_id must be omitted when desired_state is not_favorite"
                );
                return false;
            }

            validated.Add(
                new(
                    candidate.MutationId,
                    candidate.Target.Kind,
                    candidate.Target.Id,
                    candidate.DesiredState!,
                    candidate.FavoriteId
                )
            );
        }

        mutations = validated;
        failure = null;
        return true;
    }

    private static FavoriteMutationValidationFailure Invalid(string detail) =>
        new(LibraryErrorCodes.InvalidFavoriteMutation, detail);

    private static FavoriteMutationValidationFailure InvalidAt(int index, string detail) =>
        Invalid($"mutations[{index}] {detail}.");
}
