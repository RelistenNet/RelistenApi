using Relisten.Accounts.Contracts.Library;

namespace RelistenUserService.Library;

public enum FavoriteMutationFailureKind
{
    IdempotencyConflict,
    FavoriteUuidConflict,
    CatalogUnavailable,
    QuotaExceeded
}

public sealed record FavoriteMutationFailure(
    FavoriteMutationFailureKind Kind,
    string Detail,
    IReadOnlyList<Guid>? ConflictingUuids = null,
    IReadOnlyList<CatalogReference>? UnavailableReferences = null);

public sealed record FavoriteMutationExecution(
    FavoriteMutationBatchResponse? Response,
    FavoriteMutationFailure? Failure)
{
    public static FavoriteMutationExecution Succeeded(FavoriteMutationBatchResponse response) =>
        new(response, null);

    public static FavoriteMutationExecution Failed(FavoriteMutationFailure failure) =>
        new(null, failure);
}
