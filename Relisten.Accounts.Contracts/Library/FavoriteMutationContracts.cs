namespace Relisten.Accounts.Contracts.Library;

public sealed record FavoriteMutationBatchRequest(
    int ContractVersion,
    IReadOnlyList<FavoriteMutationRequestItem>? Mutations);

public sealed record FavoriteMutationRequestItem(
    Guid MutationUuid,
    string? CatalogType,
    Guid CatalogUuid,
    string? DesiredState,
    Guid? FavoriteUuid);

public sealed record FavoriteMutationBatchResponse(
    int ContractVersion,
    long LibraryRevision,
    IReadOnlyList<FavoriteMutationResult> Results);

public sealed record FavoriteMutationResult(
    Guid MutationUuid,
    string CatalogType,
    Guid CatalogUuid,
    string DesiredState,
    bool Changed,
    Guid? SubmittedFavoriteUuid,
    Guid? CanonicalFavoriteUuid,
    long LibraryRevision);
