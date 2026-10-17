using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Replaces one contributor's complete inputs and identity dependencies. A recording uses
/// a stable contributor key; another recording cannot remove its bindings. Revision zero
/// means absent. An empty replacement retains the contributor's revision as a tombstone.
/// </summary>
public sealed record NormalizationInputContribution(
    NormalizationResourceKey Contributor,
    long ExpectedRevision,
    IReadOnlyList<NormalizationResourceKey> Resources,
    IReadOnlyList<Guid> DependencySubjectUuids
);

/// <summary>
/// Current contributor ownership, read from one primary snapshot. Catalog entries are keys;
/// a new run must supply the caller's observed generations for its own catalog reads.
/// </summary>
public sealed record NormalizationInputContributionState(
    Guid SubjectUuid,
    NormalizationResourceKey Contributor,
    long Revision,
    IReadOnlyList<NormalizationResourceKey> Resources,
    IReadOnlyList<Guid> DependencySubjectUuids,
    IReadOnlyList<Guid> CatalogArtistUuids,
    IReadOnlyList<string> SourceIdentifiers,
    bool UsesMappingRegistry = false,
    IReadOnlyList<Guid>? ImportedVenueUuids = null,
    IReadOnlyList<NormalizationCandidateUniverseKey>? CandidateUniverses = null
);
