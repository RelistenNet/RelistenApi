using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api.V4;

// Wire resources for v4. Imported playback facts stay intact; normalized interpretation is an overlay.
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record ShowSummaryResource
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

    /// <summary>Listening metrics in the endpoint's scoring context; null when its map has no entry.</summary>
    public PopularityResource? Popularity { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ImportedVenueResource? ImportedVenue { get; init; }
    public ArtistVenueResource? ArtistVenue { get; init; }
    public YearResource? Year { get; init; }
    public TourResource? Tour { get; init; }
    public EraResource? Era { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record ShowResource : ShowSummaryResource
{
    public IReadOnlyList<SourceResource> Sources { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record SourceSummaryResource
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
    public SourceFlacType FlacType { get; init; }
    public int ReviewCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ImportedVenueResource? ImportedVenue { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SourceResource : SourceSummaryResource
{
    public IReadOnlyList<SourceSetResource> Sets { get; init; } = [];
    public IReadOnlyList<LinkResource> Links { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record SourceSetSummaryResource
{
    public SourceSetId Id { get; init; }
    public SourceId SourceId { get; init; }
    public ArtistId ArtistId { get; init; }
    public int Index { get; init; }
    public bool IsEncore { get; init; }
    public string Name { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record SourceSetResource : SourceSetSummaryResource
{
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
public record ImportedVenueResource
{
    /// <summary>All catalog shows assigned to this imported row, including normalized shows.</summary>
    public int ShowCount { get; init; }
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
public sealed class SongResource
{
    public SongId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public string Name { get; init; } = null!;
    public SongKind? Kind { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record VenueResource
{
    public VenueId Id { get; init; }

    /// <summary>The canonical venue's URL slug; imported artist-specific slugs remain separate.</summary>
    public string Slug { get; init; } = null!;

    /// <summary>Stored show memberships across artists approved for normalized reads, independent of request filters.</summary>
    public int GlobalShowCount { get; init; }

    public string Name { get; init; } = null!;
    public VenueKind Kind { get; init; }
    public VenueId? ParentId { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public VenueCoordinatePrecision? CoordinatePrecision { get; init; }

    /// <summary>Distinct names and alias kinds, including merged venues, in stable order.</summary>
    public IReadOnlyList<VenueNameResource> Names { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class TrackPartResource
{
    public int Ordinal { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<TrackQualifier> Qualifiers { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool SegueAfter { get; init; }
    public SongResource? Song { get; init; }
    public TrackPartState State { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record TrackNormalizationResource(
    TrackNormalizationState State,
    TrackNormalizationUnavailableReason? Reason,
    IReadOnlyList<TrackPartResource> Parts
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record YearResource
{
    /// <summary>Artist-local listening scores; null when no scored activity is stored.</summary>
    public PopularityResource? Popularity { get; init; }
    public YearId Id { get; init; }
    public ArtistId ArtistId { get; init; }
    public int ShowCount { get; init; }
    public int SourceCount { get; init; }

    /// <summary>Whether any show in this year has a soundboard recording.</summary>
    public bool HasSoundboardSource { get; init; }
    public int? Duration { get; init; }
    public float? AvgDuration { get; init; }
    public float AvgRating { get; init; }
    public string Year { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record TourResource
{
    public TourId Id { get; init; }
    public ArtistId ArtistId { get; init; }

    /// <summary>Catalog shows assigned to this tour, independent of imported setlist counts.</summary>
    public int ShowCount { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string Name { get; init; } = null!;
    public string Slug { get; init; } = null!;
    public string UpstreamIdentifier { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>A venue name or alias, independent of its stored historical periods.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record VenueNameResource(string Name, VenueNameResourceKind Kind);
