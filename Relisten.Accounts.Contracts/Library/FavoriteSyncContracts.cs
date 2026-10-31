using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Library;

public sealed record FavoriteLibrarySnapshot(
    int ContractVersion,
    long LibraryRevision,
    string NextCursor,
    IReadOnlyList<FavoriteSnapshotItem> Favorites
);

public sealed record FavoriteSnapshotItem(
    FavoriteId FavoriteId,
    FavoriteTarget Target,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record FavoriteLibraryChanges(
    int ContractVersion,
    long LibraryRevision,
    IReadOnlyList<FavoriteLibraryChange> Changes,
    string NextCursor,
    bool HasMore
);

public sealed record FavoriteLibraryChange(
    LibraryChangeId ChangeId,
    long Revision,
    string ChangeType,
    FavoriteId FavoriteId,
    FavoriteTarget Target,
    DateTimeOffset ChangedAt
);
