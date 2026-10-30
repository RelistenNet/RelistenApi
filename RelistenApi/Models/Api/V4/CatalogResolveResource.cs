using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Saved references use an explicit identity scheme; legacy aliases do not become public IDs.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class CatalogResolveRequest
{
    public IReadOnlyList<CatalogReferenceRequest?>? References { get; init; }
}

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed class CatalogReferenceRequest
{
    // Strings retain invalid input for semantic validation (422), rather than JSON
    // enum binding treating an unknown token as malformed JSON (400).
    public string? Kind { get; init; }
    public string? Scheme { get; init; }
    public string? Id { get; init; }
    public string? ArtistId { get; init; }
}

/// <summary>Results correspond to input order, including duplicates. Entity arrays are patches.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogResolveResource(
    IReadOnlyList<CatalogReferenceResolutionResource> References,
    CatalogResolverEntitiesResource Entities
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogReferenceResolutionResource(
    CatalogReferenceKind Kind,
    CatalogReferenceScheme Scheme,
    string Id,
    ArtistId? ArtistId,
    CatalogReferenceAvailability Availability,
    CatalogReferenceTargetResource? Target,
    string? Reason
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogReferenceTargetResource(CatalogReferenceKind Kind, string Id);
