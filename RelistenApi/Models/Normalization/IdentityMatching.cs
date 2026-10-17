using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public enum IdentityMatchingDomain
{
    People,
    Venues,
    Repertoire,
    Compositions,
}

public enum IdentityFeatureAgreement
{
    Missing,
    Agrees,
    Differs,
    Conflicts,
}

public enum IdentityMatchDisposition
{
    NotCandidate,
    NeedsEvidence,
    Review,
    AutomaticMatch,
}

public enum IdentityMatchRule
{
    QualifiedEntityIdentifier,
    PersonNameAndQualifiedHandle,
    VenueNameAndAddress,
    RepertoireTitleAndComposition,
    CompositionTitleAndCompleteAuthorship,
}

/// <summary>Names retain their original wording. A handle is compared only within its provider.</summary>
public sealed record IdentityMatchingName(
    string Wording,
    bool IsHandle = false,
    string? Provider = null
);

/// <summary>
/// Only identifiers asserted to identify the entity itself can support the identifier rule.
/// An upload, collection or recording's performer identifier cannot be repurposed as
/// an identifier for its taper. Account ownership must be established separately.
/// Different identifiers in one provider are not automatically contradictory: duplicate pages exist.
/// ExclusiveWithinEntity requires an authority contract that permits exactly one current ID
/// for this entity. Retired IDs must resolve through that authority's redirects first.
/// </summary>
public sealed record MatchingExternalIdentity(
    string Provider,
    string ResourceType,
    string ProviderKey,
    bool IdentifiesEntity = false,
    bool ExclusiveWithinEntity = false
);

public sealed record PersonMatchRecord(
    Guid? Uuid,
    string Kind,
    IReadOnlyList<IdentityMatchingName> Names,
    IReadOnlyList<MatchingExternalIdentity> ExternalIdentities,
    IReadOnlySet<Guid> ArtistUuids
);

/// <summary>
/// Locality keys are resolved geographic identifiers, not unstandardized city labels.
/// Addresses are retained separately; unequal address strings alone do not prove a move.
/// </summary>
public sealed record MatchingVenueLocation(
    string? CountryCode,
    string? LocalityKey,
    string? Address
);

public sealed record MatchingVenueName(
    string Wording,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    string? Language = null
);

/// <summary>
/// One supported location account. Unknown date bounds stay unknown; they do not assert that
/// the location applied forever. Known bounds must be ordered, as accepted venue assertions are
/// in storage. Provenance and original geography labels belong to the facts snapshot.
/// </summary>
public sealed record MatchingVenueLocationAssertion(
    MatchingVenueLocation Location,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null
);

/// <summary>
/// LocationAssertions is the complete supported collection when supplied, including an empty
/// collection. Only null uses the legacy single Location. ObservedDate filters a collection;
/// it does not assert that each historical member applied on that date. Never select a convenient
/// assertion or combine fields from separate location accounts before comparison.
/// </summary>
public sealed record VenueMatchRecord(
    Guid? Uuid,
    string Kind,
    Guid? ParentVenueUuid,
    IReadOnlyList<MatchingVenueName> Names,
    IReadOnlyList<MatchingExternalIdentity> ExternalIdentities,
    MatchingVenueLocation? Location,
    DateOnly? ObservedDate = null,
    IReadOnlyList<MatchingVenueLocationAssertion>? LocationAssertions = null
);

public sealed record RepertoireMatchRecord(
    Guid? Uuid,
    Guid ArtistUuid,
    IReadOnlyList<IdentityMatchingName> Titles,
    IReadOnlyList<MatchingExternalIdentity> ExternalIdentities,
    Guid? CompositionUuid = null
);

/// <summary>
/// Author UUIDs must already resolve to canonical people. Include writer/composer/lyricist
/// credits only; arrangers, cover artists and original performers do not establish authorship.
/// CompleteAuthorship means the supporting source asserts a complete author list.
/// </summary>
public sealed record CompositionMatchRecord(
    Guid? Uuid,
    IReadOnlyList<IdentityMatchingName> Titles,
    IReadOnlyList<MatchingExternalIdentity> ExternalIdentities,
    IReadOnlySet<Guid> AuthorUuids,
    bool CompleteAuthorship
);

public sealed record IdentityMatchingFeature(
    string Name,
    IdentityFeatureAgreement Agreement,
    string Reason
);

/// <summary>
/// Load this record from the active policy registry only after its evaluation receipt is verified.
/// The pure matcher cannot certify an evaluation or activate a policy. No default policy is active.
/// </summary>
public sealed record EvaluationBackedIdentityPolicy(
    string Version,
    Guid EvaluationReceiptUuid,
    IdentityMatchingDomain Domain,
    IReadOnlySet<IdentityMatchRule> EnabledRules
);

/// <summary>
/// NameTokenSimilarity is a retrieval/ranking feature, not a probability. An AutomaticMatch
/// is a pair proposal; the resolver must still check competing candidates, current evidence,
/// redirects, reviewed exclusions and the publication fence before accepting a relationship.
/// </summary>
public sealed record IdentityCandidateComparison(
    Guid? CandidateUuid,
    IdentityMatchingDomain Domain,
    IdentityMatchDisposition Disposition,
    bool IsCandidate,
    double NameTokenSimilarity,
    IReadOnlyList<IdentityMatchingFeature> Features,
    IReadOnlyList<IdentityMatchRule> SupportedRules,
    IdentityMatchRule? ProposedRule,
    string? PolicyVersion,
    Guid? EvaluationReceiptUuid
);
