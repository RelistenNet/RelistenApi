using System.Text.Json.Serialization;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Playlists;

namespace Relisten.Accounts.Contracts.Library;

public sealed record FavoriteLibrarySnapshot(
    int ContractVersion,
    long LibraryRevision,
    string NextCursor,
    IReadOnlyList<FavoriteSnapshotItem> Favorites
)
{
    public string HistoryGeneration { get; init; } = "";
    public IReadOnlyList<PlaylistSummary> Playlists { get; init; } = [];
}

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
)
{
    public string HistoryGeneration { get; init; } = "";
}

public sealed record FavoriteLibraryChange(
    LibraryChangeId ChangeId,
    long Revision,
    string ChangeType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FavoriteId? FavoriteId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FavoriteTarget? Target,
    DateTimeOffset ChangedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PlaylistSummary? Playlist = null
);
