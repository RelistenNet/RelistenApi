using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>Counts and monotonic header revisions from the same snapshot, including retained empty contributors.</summary>
public sealed record NormalizationInputContributionsCapacityStamp(
    long Contributors,
    long Bindings,
    string RevisionSum,
    long UnmanagedBindings = 0
);

/// <summary>A complete snapshot was refused; retry observes owner and membership changes without loading oversized rows.</summary>
public sealed class NormalizationInputContributionsCapacityException(
    NormalizationSubjectObservation owner,
    NormalizationInputContributionsCapacityStamp membership
)
    : InvalidOperationException(
        "The complete input contribution snapshot exceeds its contributor or binding limit; no partial snapshot was returned."
    )
{
    public NormalizationSubjectObservation Owner { get; } = owner;
    public NormalizationInputContributionsCapacityStamp Membership { get; } = membership;
}

/// <summary>
/// Replaces one contributor's complete inputs and identity dependencies. A recording uses
/// a stable contributor key; another recording cannot remove its bindings. Revision zero
/// means absent. An empty replacement retains the contributor's revision as a tombstone.
/// OwnerObservationSubjectUuids owns factual read keys without creating causal graph edges.
/// </summary>
public sealed record NormalizationInputContribution(
    NormalizationResourceKey Contributor,
    long ExpectedRevision,
    IReadOnlyList<NormalizationResourceKey> Resources,
    IReadOnlyList<Guid> DependencySubjectUuids,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<Guid>? OwnerObservationSubjectUuids = null
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
    IReadOnlyList<NormalizationCandidateUniverseKey>? CandidateUniverses = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<Guid>? OwnerObservationSubjectUuids = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<Guid>? SourceUuids = null
);

/// <summary>
/// Original evidence and catalog preconditions for one replacement. Every owned resource
/// requires its consumed revision. Keys define this contributor's complete membership;
/// different versions of a shared key cannot be combined into one prepared batch.
/// </summary>
public sealed record NormalizationPreparedInputContribution(
    NormalizationInputContribution Contribution,
    IReadOnlyList<NormalizationBindingInputReference> ExpectedInputRevisions,
    IReadOnlyList<NormalizationCatalogDependency>? CatalogInputs = null,
    IReadOnlyList<NormalizationSourceIdentityDependency>? SourceIdentities = null,
    NormalizationMappingRegistryDependency? MappingRegistry = null,
    IReadOnlyList<NormalizationImportedVenueDependency>? ImportedVenues = null,
    IReadOnlyList<NormalizationCandidateUniverseDependency>? CandidateUniverses = null,
    IReadOnlyList<NormalizationSubjectObservation>? ExpectedOwnerObservations = null,
    IReadOnlyList<NormalizationSourceUuidDependency>? SourceUuids = null
);

/// <summary>Oversized snapshots fail before materializing bindings; no partial result is returned.</summary>
public sealed record NormalizationInputContributionsLimits(
    int MaximumContributors = 100,
    int MaximumBindings = 10000
);

/// <summary>
/// Owner state and complete contributor memberships from one primary read-only snapshot.
/// UnmanagedBaseline exposes pre-contribution bindings with revision zero and their reserved
/// baseline key. It is not persisted by this read; first contributed admission adopts those
/// bindings under the existing baseline contract. Consumers must handle or reject it explicitly.
/// </summary>
public sealed record NormalizationInputContributionsSnapshot(
    NormalizationSubjectState? Owner,
    IReadOnlyList<NormalizationInputContributionState> Contributions,
    NormalizationInputContributionState? UnmanagedBaseline = null
);

/// <summary>A complete contributor-membership observation, including retained empty tombstones.</summary>
public sealed record NormalizationInputContributionRevision(
    NormalizationResourceKey Contributor,
    long Revision
);
