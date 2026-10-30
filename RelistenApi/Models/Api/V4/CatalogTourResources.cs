using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Relisten.Api.Models.Api.V4;

/// <summary>A tour and its complete catalog show collection; show summaries remain compact.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record TourWithShowsResource : TourResource
{
    public IReadOnlyList<ShowSummaryResource> Shows { get; init; } = [];
}
