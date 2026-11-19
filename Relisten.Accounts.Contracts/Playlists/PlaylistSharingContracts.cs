using System.Text.Json.Serialization;
using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Playlists;

public sealed record SetPlaylistPublicationRequest(
    int ContractVersion,
    CommandId CommandId,
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
