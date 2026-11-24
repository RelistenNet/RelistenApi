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
/// of its tracks. <c>RecentPlayCount</c> counts plays started from it on the last 30 UTC
/// days, from signed-in and anonymous listeners. A day's plays count once it is
/// materialized, an hour after it closes. <c>Description</c> is the author's Markdown, or
/// null when there is none. <c>ArtistIds</c> are the typed v4 IDs (<c>artist_…</c>) of the
/// artists its tracks belong to, the artist with the most tracks first; tracks missing from
/// the catalog add none.
/// </summary>
public sealed record PublicPlaylistListing(
    PlaylistId PlaylistId,
    string PublicCode,
    string Name,
    string? Description,
    PublicPlaylistOwner Owner,
    int TrackCount,
    int DurationSeconds,
    DateTimeOffset UpdatedAt,
    int FollowerCount,
    long RecentPlayCount,
    IReadOnlyList<string> ArtistIds
);

/// <summary>A page of public playlists. A null <c>NextCursor</c> ends the listing.</summary>
public sealed record PublicPlaylistPage(
    int ContractVersion,
    IReadOnlyList<PublicPlaylistListing> Playlists,
    string? NextCursor
);
