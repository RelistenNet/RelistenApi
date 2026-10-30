using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

/// <summary>An imported review. Text and nullable ratings retain their upstream values.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class SourceReviewResource
{
    public SourceReviewId Id { get; init; }
    public SourceId SourceId { get; init; }
    public int? Rating { get; init; }
    public string? Title { get; init; }
    public string Review { get; init; } = null!;
    public string? Author { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
