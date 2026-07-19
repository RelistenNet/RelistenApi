namespace Relisten.Accounts.Contracts.Library;

public sealed record FavoriteLibrarySnapshot(
    int ContractVersion,
    long LibraryRevision,
    string NextCursor,
    IReadOnlyList<FavoriteSnapshotItem> Favorites);

public sealed record FavoriteSnapshotItem(
    Guid FavoriteUuid,
    string CatalogType,
    Guid CatalogUuid,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FavoriteLibraryChanges(
    int ContractVersion,
    long LibraryRevision,
    IReadOnlyList<FavoriteLibraryChange> Changes,
    string NextCursor,
    bool HasMore);

public sealed record FavoriteLibraryChange(
    Guid ChangeUuid,
    long Revision,
    string ChangeType,
    Guid FavoriteUuid,
    string CatalogType,
    Guid CatalogUuid,
    DateTimeOffset ChangedAt);
