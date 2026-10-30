using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Artist-owned canonical song. Counts describe exact active recording matches, not setlist appearances.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record SongDetailResource
{
    public SongId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public string Name { get; init; } = null!;
    public SongKind? Kind { get; init; }
    public string Slug { get; init; } = null!;
    public string? OriginalArtist { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public int MatchedTrackCount { get; init; }
    public int MatchedShowCount { get; init; }
}

/// <summary>A song and its complete recording-backed show collection.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SongWithShowsResource : SongDetailResource
{
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
}

/// <summary>A song and its complete playable-file matches, without show membership.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SongWithTracksResource : SongDetailResource
{
    public IReadOnlyList<SongTrackMatchResource> Tracks { get; init; } = [];
}

/// <summary>Imported setlist song. The count is distinct catalog shows supported by imported setlist evidence.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record ImportedSongResource
{
    public ImportedSongId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public string Name { get; init; } = null!;
    public string Slug { get; init; } = null!;
    public string UpstreamIdentifier { get; init; } = null!;
    public int SetlistShowCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ImportedSongWithShowsResource : ImportedSongResource
{
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
}

/// <summary>Browse target. The server selects canonical or imported identity using approved evidence.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ArtistSongEntryResource(
    SongEntryKind Kind,
    SongDetailResource? Song,
    ImportedSongResource? ImportedSong
);

/// <summary>One playable file, even if the requested song occurs in several of its parts.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SongTrackMatchResource(
    TrackResource Track,
    IReadOnlyList<int> MatchingPartOrdinals
);
