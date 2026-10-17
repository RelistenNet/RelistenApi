using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Bounds apply to each returned candidate, except MaximumCandidates. Credit witnesses are
/// examples of an exact artist aggregate; limiting those examples does not truncate membership.
/// A reader must report a blocking issue whenever another bound omits matching facts.
/// </summary>
public sealed record CandidateFactsLimits
{
    public int MaximumCandidates { get; init; } = 256;
    public int MaximumIdentityClosure { get; init; } = 1024;
    public int MaximumAssertionsPerKind { get; init; } = 5000;
    public int MaximumEvidencePerAssertion { get; init; } = 256;
    public int MaximumArtistContexts { get; init; } = 10000;
    public int MaximumRoleBasisGroupsPerArtist { get; init; } = 128;
    public int MaximumWitnessesPerArtist { get; init; } = 3;
    public int MaximumMissingContextWitnesses { get; init; } = 16;
    public int MaximumParentClosure { get; init; } = 1024;
}

public enum CandidateFactsIssueKind
{
    CandidateLimit,
    IdentityClosureLimit,
    AssertionLimit,
    EvidenceLimit,
    ArtistContextLimit,
    RoleBasisGroupLimit,
    ParentClosureLimit,
    MissingOwner,
    MissingCatalogClock,
    MissingRelatedIdentity,
    InvalidStoredFact,
    UnknownAuthority,
    UnknownGeography,
    MissingCatalogContext,
}

/// <summary>
/// Missing freshness stamps and omitted facts prevent automatic acceptance. Unmodeled
/// authority or geography remain diagnostics: individual matching rules decide whether they
/// require that information. Retrieval itself never accepts an identity.
/// </summary>
public sealed record CandidateFactsIssue(
    CandidateFactsIssueKind Kind,
    Guid? CandidateUuid = null,
    string? FactKind = null
)
{
    public bool PreventsAutomaticAcceptance =>
        Kind
            is not (
                CandidateFactsIssueKind.UnknownAuthority
                or CandidateFactsIssueKind.UnknownGeography
                or CandidateFactsIssueKind.MissingCatalogContext
            );
}

/// <summary>Original stamps read with facts, not refreshed after selection or admission.</summary>
public sealed record CandidateFactsReadSet(
    IReadOnlyList<NormalizationCandidateUniverseDependency> CandidateUniverses,
    IReadOnlyList<NormalizationSubjectObservation> OwnerObservations,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs
);

/// <summary>
/// Selection, facts and freshness share one primary repeatable-read snapshot. QueryHash covers
/// the frozen query, limits and reader version. Complete describes the projection, not confidence
/// in an identity match. Missing source context and bounded witness examples remain explicit.
/// </summary>
public sealed record NormalizationCandidateFactsSnapshot<T>(
    string Version,
    string QueryHash,
    NormalizationCandidateQuery Query,
    CandidateFactsLimits Limits,
    NormalizationCandidateSet Selection,
    IReadOnlyList<T> Facts,
    CandidateFactsReadSet ReadSet,
    IReadOnlyList<CandidateFactsIssue> Issues
)
{
    public bool Complete =>
        !Selection.NameCandidatesTruncated
        && !Issues.Any(issue => issue.PreventsAutomaticAcceptance);
}

/// <summary>
/// StoredIdentityUuid is the endpoint on this current assertion before redirect resolution.
/// OwnerSubjectUuid is its editorial owner, which can be a recording rather than that person.
/// Evidence comes from the accepted revision; music uses MusicRevisionUuid's immutable JSON,
/// never the mutable evidence-search index. A missing owner is an explicit issue.
/// </summary>
public sealed record CandidateAssertionProvenance(
    Guid AssertionUuid,
    Guid StoredIdentityUuid,
    Guid? OwnerSubjectUuid,
    long Revision,
    string Basis,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    Guid? MusicRevisionUuid = null
);

/// <summary>
/// Current identity state has no invented assertion citation. Reverse-closure headers retain
/// searchable names of retired identities separately from accepted aliases and title revisions.
/// </summary>
public sealed record CandidateIdentityHeader(
    Guid Uuid,
    string DisplayText,
    long Revision,
    Guid? OwnerSubjectUuid,
    string? Kind = null,
    bool? MetadataReviewed = null,
    Guid? ArtistUuid = null
);

public sealed record CandidateRedirectFact(
    Guid OriginalUuid,
    Guid SurvivorUuid,
    Guid? DecisionUuid = null,
    Guid? MusicRevisionUuid = null
);

public sealed record CandidateNameFact(
    CandidateAssertionProvenance Provenance,
    string Wording,
    string? Language = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null
);

/// <summary>A qualified key is retained without inferring exclusivity or provider authority.</summary>
public sealed record CandidateExternalIdentityFact(
    CandidateAssertionProvenance Provenance,
    string Provider,
    string ResourceType,
    string ProviderKey
);

public enum CandidateCreditContextStatus
{
    Artist,
    MissingSource,
    MissingArtist,
}

public sealed record RecordingCreditWitness(
    CandidateAssertionProvenance Provenance,
    Guid SourceUuid,
    string Role,
    string OriginalText,
    Guid? ScopeTrackUuid,
    string ReasonCode,
    CandidateCreditContextStatus ContextStatus,
    Guid? ArtistUuid = null
);

public sealed record CreditRoleBasisCount(string Role, string Basis, long Count);

/// <summary>
/// Counts include every accepted credit through the deduplicated person redirect closure,
/// including recordings without playable files. Witnesses are bounded supporting examples;
/// their bases do not characterize the entire aggregate.
/// </summary>
public sealed record PersonArtistContextFact(
    Guid ArtistUuid,
    long AcceptedCreditCount,
    IReadOnlyList<CreditRoleBasisCount> Counts,
    IReadOnlyList<RecordingCreditWitness> Witnesses,
    bool WitnessesTruncated
);

public sealed record PersonCandidateFacts
{
    public required NormalizationCandidateHit Hit { get; init; }
    public required CandidateIdentityHeader Header { get; init; }
    public IReadOnlyList<CandidateIdentityHeader> IdentityClosure { get; init; } = [];
    public IReadOnlyList<CandidateRedirectFact> Redirects { get; init; } = [];
    public IReadOnlyList<CandidateNameFact> Aliases { get; init; } = [];
    public IReadOnlyList<CandidateExternalIdentityFact> ExternalIdentities { get; init; } = [];
    public IReadOnlyList<PersonArtistContextFact> ArtistContexts { get; init; } = [];
    public long MissingSourceCreditCount { get; init; }
    public long MissingArtistCreditCount { get; init; }
    public IReadOnlyList<RecordingCreditWitness> MissingContextWitnesses { get; init; } = [];
    public bool MissingContextWitnessesTruncated { get; init; }
}

/// <summary>Raw accepted location assertions; country and city strings are not resolved keys.</summary>
public sealed record CandidateVenueLocationFact(
    CandidateAssertionProvenance Provenance,
    string OriginalLocality,
    string? Country,
    string? Region,
    string? City,
    string? Address,
    double? Latitude,
    double? Longitude,
    string? CoordinatePrecision,
    DateOnly? ValidFrom,
    DateOnly? ValidTo
);

/// <summary>Retains the stored relationship separately from its current parent resolution.</summary>
public sealed record CandidateVenueParentFact(
    Guid StoredVenueUuid,
    Guid? StoredParentVenueUuid,
    Guid? ResolvedParentVenueUuid,
    IReadOnlyList<CandidateIdentityHeader> ParentIdentityClosure,
    IReadOnlyList<CandidateRedirectFact> ParentRedirects
);

public sealed record VenueCandidateFacts
{
    public required NormalizationCandidateHit Hit { get; init; }
    public required CandidateIdentityHeader Header { get; init; }
    public IReadOnlyList<CandidateIdentityHeader> IdentityClosure { get; init; } = [];
    public IReadOnlyList<CandidateRedirectFact> Redirects { get; init; } = [];
    public IReadOnlyList<CandidateNameFact> Names { get; init; } = [];
    public IReadOnlyList<CandidateVenueLocationFact> Locations { get; init; } = [];
    public IReadOnlyList<CandidateExternalIdentityFact> ExternalIdentities { get; init; } = [];
    public IReadOnlyList<CandidateVenueParentFact> Parents { get; init; } = [];
}

/// <summary>One accepted music identity revision owns its title and alias list together.</summary>
public sealed record CandidateMusicTitleFact(
    CandidateAssertionProvenance Provenance,
    string DisplayTitle,
    IReadOnlyList<string> Aliases
);

public sealed record RepertoireCompositionFact(
    CandidateAssertionProvenance Provenance,
    Guid StoredRepertoireSongUuid,
    Guid StoredCompositionUuid,
    Guid? ResolvedCompositionUuid,
    string MatchReason,
    NormalizationResourceKey? MatchedWorkIdentity,
    IReadOnlyList<Guid> MatchedAuthorUuids,
    bool CompleteAuthorship
);

public sealed record RepertoireCandidateFacts
{
    public required NormalizationCandidateHit Hit { get; init; }
    public required CandidateIdentityHeader Header { get; init; }
    public required Guid ArtistUuid { get; init; }
    public IReadOnlyList<CandidateIdentityHeader> IdentityClosure { get; init; } = [];
    public IReadOnlyList<CandidateRedirectFact> Redirects { get; init; } = [];
    public IReadOnlyList<CandidateMusicTitleFact> Titles { get; init; } = [];
    public IReadOnlyList<CandidateExternalIdentityFact> ExternalIdentities { get; init; } = [];
    public IReadOnlyList<RepertoireCompositionFact> CompositionLinks { get; init; } = [];
    public IReadOnlyList<CandidateIdentityHeader> CompositionIdentities { get; init; } = [];
    public IReadOnlyList<CandidateRedirectFact> CompositionRedirects { get; init; } = [];
}

/// <summary>Person and catalog artist credits remain distinct, and every role is retained.</summary>
public sealed record CompositionWorkCreditFact(
    CandidateAssertionProvenance Provenance,
    Guid StoredCompositionUuid,
    string Role,
    string OriginalText,
    Guid? StoredPersonUuid,
    Guid? ResolvedPersonUuid,
    Guid? ArtistUuid
);

public sealed record CandidatePersonCreditTarget(
    Guid StoredPersonUuid,
    Guid? ResolvedPersonUuid,
    IReadOnlyList<CandidateIdentityHeader> IdentityClosure,
    IReadOnlyList<CandidateRedirectFact> Redirects
);

public sealed record CandidateArtistCreditTarget(
    Guid ArtistUuid,
    bool Exists,
    string? Name,
    string? Slug
);

public sealed record CompositionCandidateFacts
{
    public required NormalizationCandidateHit Hit { get; init; }
    public required CandidateIdentityHeader Header { get; init; }
    public IReadOnlyList<CandidateIdentityHeader> IdentityClosure { get; init; } = [];
    public IReadOnlyList<CandidateRedirectFact> Redirects { get; init; } = [];
    public IReadOnlyList<CandidateMusicTitleFact> Titles { get; init; } = [];
    public IReadOnlyList<CandidateExternalIdentityFact> ExternalIdentities { get; init; } = [];
    public IReadOnlyList<CompositionWorkCreditFact> WorkCredits { get; init; } = [];

    // These targets cover every credit role. Only WorkCredits in writer/composer/lyricist
    // roles supply authorship; resolving an arranger or performer does not make them an author.
    public IReadOnlyList<CandidatePersonCreditTarget> PersonCreditTargets { get; init; } = [];
    public IReadOnlyList<CandidateArtistCreditTarget> ArtistCreditTargets { get; init; } = [];

    // The current work-credit model supplies individual claims, not an exhaustive author list.
    public bool CompleteAuthorship => false;
}
