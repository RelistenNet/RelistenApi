using System;

namespace Relisten.Services.Popularity;

/// <summary>The comparison population refreshed by one existing calculation.</summary>
public enum PopularityPopulationKind
{
    Artist,
    Show,
    Year,
}

/// <summary>A null artist selects the global population.</summary>
public sealed record PopularityPopulation(PopularityPopulationKind Kind, Guid? Artist = null)
{
    internal string StorageKind => Kind.ToString().ToLowerInvariant();
}
