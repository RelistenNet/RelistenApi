using Relisten.Accounts.Contracts.Library;

namespace RelistenUserService.Library;

public enum FavoriteMutationFailureKind
{
    IdempotencyConflict,
    FavoriteIdConflict,
    QuotaExceeded,
}

public sealed record FavoriteMutationFailure(
    FavoriteMutationFailureKind Kind,
    string Detail,
    IReadOnlyList<string>? ConflictingIds = null
);

public sealed record FavoriteMutationExecution(
    FavoriteMutationBatchResponse? Response,
    FavoriteMutationFailure? Failure
)
{
    public static FavoriteMutationExecution Succeeded(FavoriteMutationBatchResponse response) =>
        new(response, null);

    public static FavoriteMutationExecution Failed(FavoriteMutationFailure failure) =>
        new(null, failure);
}
