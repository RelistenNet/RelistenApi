using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Library;

public sealed record FavoriteMutationBatchRequest(
    int ContractVersion,
    IReadOnlyList<FavoriteMutationRequestItem>? Mutations
);

public sealed record FavoriteMutationRequestItem(
    MutationId MutationId,
    FavoriteTarget? Target,
    string? DesiredState,
    FavoriteId? FavoriteId
);

public sealed record FavoriteMutationBatchResponse(
    int ContractVersion,
    long LibraryRevision,
    IReadOnlyList<FavoriteMutationResult> Results
);

public sealed record FavoriteMutationResult(
    MutationId MutationId,
    FavoriteTarget Target,
    string DesiredState,
    bool Changed,
    FavoriteId? SubmittedFavoriteId,
    FavoriteId? CanonicalFavoriteId,
    long LibraryRevision
);
