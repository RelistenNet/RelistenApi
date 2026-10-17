using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Catalog association only; a bound row does not accept a normalized interpretation.</summary>
public enum NormalizationCatalogBindingStatus
{
    Bound,
    Missing,
    Ambiguous,
    ProviderConflict,
}

/// <summary>Only Bound supplies a value. Reasons distinguish missing context from conflicting evidence.</summary>
public sealed record NormalizationCatalogBinding<T>(
    NormalizationCatalogBindingStatus Status,
    T? Value,
    string Reason
)
    where T : class;

/// <summary>
/// Actual current catalog identities. Missing or inconsistent derived context does not prevent
/// recording-credit work; consumers that need a show or venue must check ContextIssues.
/// </summary>
public sealed record NormalizationCatalogRecordingReference(
    Guid ArtistUuid,
    Guid SourceUuid,
    Guid? ShowUuid,
    Guid? SourceVenueUuid,
    IReadOnlyList<string> ContextIssues
);

/// <summary>An exact active media association with matching recording, artist and source-set ownership.</summary>
public sealed record NormalizationCatalogFileReference(
    Guid ArtistUuid,
    Guid SourceUuid,
    Guid SourceSetUuid,
    Guid TrackUuid,
    string MediaUrl
);

/// <summary>The original file assertion remains available when its current catalog association is unresolved.</summary>
public sealed record NormalizationFileBinding(
    ProviderFileObservation Observation,
    NormalizationCatalogBinding<NormalizationCatalogFileReference> Binding
);

/// <summary>
/// One primary repeatable-read snapshot. Both CatalogInputs and SourceIdentities must join the
/// publication read set. The snapshot itself does not fence later changes or accept provider claims.
/// </summary>
public sealed record NormalizationRecordingBindingSnapshot(
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    NormalizationCatalogBinding<NormalizationCatalogRecordingReference> Recording,
    IReadOnlyList<NormalizationFileBinding> Files,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs,
    IReadOnlyList<NormalizationSourceIdentityDependency> SourceIdentities
);
