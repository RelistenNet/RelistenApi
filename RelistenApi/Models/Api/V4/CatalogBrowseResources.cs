using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

// Browse resources retain typed navigation identities. Collection results contain
// summaries; only detail resources claim ownership of recording hierarchies.
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record ArtistResource
{
    public ArtistId Id { get; init; }
    public string Name { get; init; } = null!;
    public string Slug { get; init; } = null!;
    public string? SortName { get; init; }
    public string? MusicbrainzId { get; init; }

    /// <summary>Existing featured/auto-created flags, preserved as a bitmask.</summary>
    public int Featured { get; init; }
    public int ShowCount { get; init; }
    public int SourceCount { get; init; }

    /// <summary>Global listening scores; null when no scored activity is stored.</summary>
    public PopularityResource? Popularity { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Unsigned 64-bit capability mask as a decimal string. See ArtistFeatures.</summary>
    public string Features { get; init; } = "0";
    public IReadOnlyList<ArtistProviderResource> UpstreamSources { get; init; } = [];
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class ArtistProviderResource
{
    /// <summary>Known upstream provider; null for an unrecognized catalog provider.</summary>
    public UpstreamProvider? Provider { get; init; }
    public string? UpstreamIdentifier { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record YearWithShowsResource : YearResource
{
    /// <summary>The complete year collection; summaries do not claim source/track ownership.</summary>
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
}
