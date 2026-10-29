using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api.V4;

// Wire resources for v4. Imported playback facts stay intact; normalized interpretation is an overlay.
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ShowResource
{
    public ShowId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public YearId? YearId { get; init; }
    public TourId? TourId { get; init; }
    public DateTime Date { get; init; }
    public string DisplayDate { get; init; } = null!;
    public float AvgRating { get; init; }
    public float? AvgDuration { get; init; }
    public DateTime MostRecentSourceUpdatedAt { get; init; }
    public bool HasSoundboardSource { get; init; }
    public bool HasStreamableFlacSource { get; init; }
    public int SourceCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ImportedVenueResource? ImportedVenue { get; init; }
    public NormalizedVenueResource? NormalizedVenue { get; init; }
    public VenueDisplayResource VenueDisplay { get; init; } = null!;
    public YearResource? Year { get; init; }
    public TourResource? Tour { get; init; }
    public EraResource? Era { get; init; }
    public IReadOnlyList<SourceResource> Sources { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class SourceResource
{
    public SourceId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public ShowId ShowId { get; init; }
    public string DisplayDate { get; init; } = null!;
    public bool IsSoundboard { get; init; }
    public bool IsRemaster { get; init; }
    public bool HasJamcharts { get; init; }
    public double AvgRating { get; init; }
    public int NumReviews { get; init; }
    public int? NumRatings { get; init; }
    public double AvgRatingWeighted { get; init; }
    public double Duration { get; init; }
    public string UpstreamIdentifier { get; init; } = null!;
    public string? Description { get; init; }
    public string? TaperNotes { get; init; }
    public string? Source { get; init; }
    public string? Taper { get; init; }
    public string? Transferrer { get; init; }
    public string? Lineage { get; init; }
    public string FlacType { get; init; } = null!;
    public int ReviewCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ImportedVenueResource? ImportedVenue { get; init; }
    public IReadOnlyList<SourceSetResource> Sets { get; init; } = [];
    public IReadOnlyList<LinkResource> Links { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class SourceSetResource
{
    public SourceSetId Id { get; init; }
    public SourceId SourceId { get; init; }
    public ArtistId ArtistId { get; init; }
    public int Index { get; init; }
    public bool IsEncore { get; init; }
    public string Name { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public IReadOnlyList<TrackResource> Tracks { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class TrackResource
{
    public TrackId Id { get; init; }
    public SourceId SourceId { get; init; }
    public SourceSetId SourceSetId { get; init; }
    public ShowId ShowId { get; init; }
    public ArtistId ArtistId { get; init; }
    public int TrackPosition { get; init; }
    public int? Duration { get; init; }
    public string Title { get; init; } = null!;
    public string Slug { get; init; } = null!;
    public string? Mp3Url { get; init; }
    public string? Mp3Md5 { get; init; }
    public string? FlacUrl { get; init; }
    public string? FlacMd5 { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public TrackNormalizationResource Normalization { get; init; } = null!;
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ImportedVenueResource
{
    public ImportedVenueId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Name { get; init; }
    public string? Location { get; init; }
    public string? UpstreamIdentifier { get; init; }
    public string? Slug { get; init; }
    public string? PastNames { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class LinkResource
{
    public SourceId SourceId { get; init; }
    public string Provider { get; init; } = null!;
    public bool ForReviews { get; init; }
    public bool ForRatings { get; init; }
    public bool ForSource { get; init; }
    public string Url { get; init; } = null!;
    public string Label { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class EraResource
{
    public int Order { get; init; }
    public string Name { get; init; } = null!;
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class NormalizedSongResource
{
    public SongId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public string Name { get; init; } = null!;
    public string? Kind { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class NormalizedVenueResource
{
    public VenueId Id { get; init; }
    public string Name { get; init; } = null!;
    public string Kind { get; init; } = null!;
    public VenueId? ParentId { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? CoordinatePrecision { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class TrackPartResource
{
    public int Ordinal { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<string> Qualifiers { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool SegueAfter { get; init; }
    public NormalizedSongResource? Song { get; init; }
    public string State { get; init; } = null!;
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record VenueDisplayResource(string? Name, string? Location);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record TrackNormalizationResource(
    string State,
    string? Reason,
    IReadOnlyList<TrackPartResource> Parts
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class YearResource
{
    public YearId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public int ShowCount { get; init; }
    public int SourceCount { get; init; }
    public int? Duration { get; init; }
    public float? AvgDuration { get; init; }
    public float AvgRating { get; init; }
    public string Year { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class TourResource
{
    public TourId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string Name { get; init; } = null!;
    public string Slug { get; init; } = null!;
    public string UpstreamIdentifier { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
