using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Retained imported venue facts and their original producer revisions. The same revisions
/// are embedded in the immutable payload; callers cannot attach fresh clocks to older wording.
/// A venue can have no artist, so ImportedVenues is required independently of CatalogInputs.
/// </summary>
public sealed record NormalizationImportedVenueSnapshot(
    NormalizationCaptureResult Capture,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
    IReadOnlyList<NormalizationImportedVenueDependency> ImportedVenues
);

/// <summary>
/// One complete imported artist dictionary and its original artist revision, also retained
/// in the payload. A known artist with no songs has a valid empty dictionary.
/// </summary>
public sealed record NormalizationArtistRepertoireSnapshot(
    NormalizationCaptureResult Capture,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs
);

/// <summary>An existing imported row, not a canonical physical-venue identity.</summary>
public sealed record NormalizationCatalogImportedVenueReference(Guid VenueUuid, Guid? ArtistUuid);

/// <summary>An existing artist-owned imported song, not an accepted repertoire or work identity.</summary>
public sealed record NormalizationCatalogImportedSongReference(
    Guid ArtistUuid,
    Guid ImportedSongUuid
);

/// <summary>
/// Checked venue facts and ownership. Pass all catalog dependencies and the exact Inputs
/// into admission; publication must also check the imported-venue revision.
/// </summary>
public sealed record NormalizationImportedVenueBindingSnapshot(
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    NormalizationCatalogBinding<NormalizationCatalogImportedVenueReference> Venue,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
    IReadOnlyList<NormalizationImportedVenueDependency> ImportedVenues
)
{
    public IReadOnlyList<NormalizationBindingInputReference> Inputs =>
        [new(InputRevisionUuid, Resource)];
}

/// <summary>Preserves a decoded imported song even when its title is missing.</summary>
public sealed record NormalizationImportedSongBinding(
    ProviderRepertoireObservation Observation,
    NormalizationCatalogBinding<NormalizationCatalogImportedSongReference> Binding
);

/// <summary>
/// Checked complete imported dictionary. Nested song origins identify rows, while Inputs
/// identifies the containing retained dictionary. No song or composition match is accepted.
/// </summary>
public sealed record NormalizationRepertoireBindingSnapshot(
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    NormalizationCatalogBinding<NormalizationCatalogArtistReference> Artist,
    IReadOnlyList<NormalizationImportedSongBinding> Songs,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs
)
{
    public IReadOnlyList<NormalizationBindingInputReference> Inputs =>
        [new(InputRevisionUuid, Resource)];
}
