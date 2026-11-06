using System;
using System.Collections.Generic;

namespace Relisten.Catalog;

public enum CatalogRecency
{
    Performed,
    Updated,
    Added,
}

public enum CatalogSort
{
    Default,
    DateDescending,
    Rating,
    Popularity,
    Updated,
    Added,
}

/// <summary>Explicit public browse filters. A show availability filter tests existence;
/// it does not prune a subsequently selected source collection.</summary>
public sealed record CatalogFilters(
    Guid? Artist = null,
    DateTime? From = null,
    DateTime? Through = null,
    string? Venue = null,
    string? Song = null,
    bool? Soundboard = null,
    bool? Flac = null,
    string? Search = null,
    bool IncludeAutoCreated = false,
    int? Month = null,
    int? Day = null,
    CatalogRecency? Recency = null,
    int? RecentDays = null,
    bool RequireSources = false,
    Guid? ShowUuid = null,
    bool TopSourceOnly = false,
    Guid[]? Artists = null
);

public sealed record CatalogPageKey(
    double Score,
    string Label,
    string Id,
    double Secondary = 0,
    string DescendingLabel = ""
);

public sealed record CatalogPageRequest(
    int First,
    CatalogPageKey? After = null,
    bool Count = false
);

public sealed record CatalogPageSelection(
    IReadOnlyList<CatalogResolvedTarget> Targets,
    IReadOnlyList<CatalogPageKey> Keys,
    bool HasNextPage,
    long? TotalCount
);

/// <summary>A relationship's owner is an opaque public ID, decoded only at the read boundary.</summary>
public sealed record CatalogCollection(
    CatalogReferenceKind Kind,
    CatalogReferenceKind? OwnerKind = null,
    string? Owner = null,
    CatalogFilters? Filters = null,
    CatalogSort Sort = CatalogSort.Default,
    bool IncludeFallback = false
);
