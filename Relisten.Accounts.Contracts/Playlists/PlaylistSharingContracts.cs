using System.Text.Json.Serialization;
using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Playlists;

/// <summary>
/// Sets the desired publication state. Repeating a request converges on the same state,
/// so it carries no command ID or receipt.
/// </summary>
public sealed record SetPlaylistPublicationRequest(
    int ContractVersion,
    [property: JsonRequired] bool Published
);

public sealed record PlaylistPublication(
    int ContractVersion,
    string HistoryGeneration,
    PlaylistId PlaylistId,
    bool Published,
    string? PublicCode,
    bool Changed
);

public sealed record CopyPublicPlaylistRequest(int ContractVersion, CommandId CommandId);

public sealed record ReportPublicPlaylistRequest(string Reason);

public sealed record PublicPlaylistOwner(UserId Id, string Username);

public sealed record PublicPlaylistSnapshot(
    int ContractVersion,
    string HistoryGeneration,
    PlaylistId PlaylistId,
    string PublicCode,
    long Revision,
    PublicPlaylistOwner Owner,
    PlaylistMetadata Metadata,
    IReadOnlyList<PlaylistSegmentSnapshot> Segments
);

/// <summary>Unavailable heads contain only the playlist ID and availability.</summary>
public sealed record FollowedPlaylistHead(PlaylistId PlaylistId, bool Available)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicPlaylistOwner? Owner { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Revision { get; init; }
}

/// <summary>
/// One public playlist in a listing. <c>DurationSeconds</c> totals the catalog durations
/// of its tracks. <c>RecentPlayCount</c> counts plays started from it in the last 30 days,
/// from signed-in and anonymous listeners, and lags by up to about two and a half hours.
/// </summary>
public sealed record PublicPlaylistListing(
    PlaylistId PlaylistId,
    string PublicCode,
    string Name,
    PublicPlaylistOwner Owner,
    int TrackCount,
    int DurationSeconds,
    DateTimeOffset UpdatedAt,
    int FollowerCount,
    long RecentPlayCount
);

/// <summary>A page of public playlists. A null <c>NextCursor</c> ends the listing.</summary>
public sealed record PublicPlaylistPage(
    int ContractVersion,
    IReadOnlyList<PublicPlaylistListing> Playlists,
    string? NextCursor
);
