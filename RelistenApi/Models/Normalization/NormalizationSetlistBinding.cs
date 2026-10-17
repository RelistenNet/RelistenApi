using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>An existing catalog artist association; no global performer identity is allocated or accepted.</summary>
public sealed record NormalizationCatalogArtistReference(Guid ArtistUuid);

/// <summary>Existing imported account and its current catalog venue; this does not normalize venue wording.</summary>
public sealed record NormalizationCatalogSetlistReference(
    Guid ArtistUuid,
    Guid SetlistShowUuid,
    Guid? VenueUuid
);

/// <summary>An existing performance association, not an accepted song sequence or composition match.</summary>
public sealed record NormalizationCatalogPerformanceReference(
    Guid ArtistUuid,
    Guid ShowUuid,
    IReadOnlyList<Guid> SupportingSourceUuids,
    string Basis
);

/// <summary>
/// Independent associations for one retained setlist account. Missing legacy metadata or an
/// unresolved performance leaves the original account available; it does not withhold recordings.
/// Inputs and all catalog dependencies must join work admission and publication.
/// ArtistScopeUuid is populated only for an intentionally artist-scoped lookup. Such a result
/// makes no claim that the account is unique across other catalog artists.
/// </summary>
public sealed record NormalizationSetlistBindingSnapshot(
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    ProviderObservationOrigin Account,
    NormalizationCatalogBinding<NormalizationCatalogArtistReference> Artist,
    NormalizationCatalogBinding<NormalizationCatalogSetlistReference> ImportedSetlist,
    NormalizationCatalogBinding<NormalizationCatalogPerformanceReference> Performance,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
    NormalizationMappingRegistryDependency MappingRegistry,
    Guid? ArtistScopeUuid = null
)
{
    /// <summary>Exact retained revisions required at admission, including any supporting account inputs.</summary>
    public IReadOnlyList<NormalizationBindingInputReference> Inputs =>
        [new(InputRevisionUuid, Resource)];
}
