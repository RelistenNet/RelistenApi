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
    public IReadOnlyList<SongShowResource> Shows { get; init; } = [];
}

/// <summary>A recording-backed show with the requested song's file-duration statistics.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SongShowResource(ShowSummaryResource Show, SongShowTimingsResource Timings);

/// <summary>
/// Whole-file durations in seconds, not song-segment timings. Each matched file counts once.
/// Null buckets have no positive, known durations. Single-song files have fully resolved
/// parts identifying only this song (resolved non-song parts are ignored). Multi-song files
/// contain this song and at least one other canonical song, even if other parts are unresolved.
/// Files with only this song plus unresolved parts contribute to neither bucket.
/// </summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SongShowTimingsResource(
    TrackDurationStatisticsResource? SingleSong,
    TrackDurationStatisticsResource? MultiSong
);

/// <summary>Statistics across positive, known file durations; even medians average the middle pair.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record TrackDurationStatisticsResource(
    int TrackCount,
    int Min,
    int Max,
    double Avg,
    double Median
);

/// <summary>
/// A song and its complete playable files, each returned once. Matching normalization
/// parts reference this song's ID; the collection does not replace show membership.
/// </summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SongWithTracksResource : SongDetailResource
{
    public IReadOnlyList<TrackResource> Tracks { get; init; } = [];
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
