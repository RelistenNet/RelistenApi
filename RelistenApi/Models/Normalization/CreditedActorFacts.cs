using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Api.Models.Normalization;

/// <summary>Source-scoped relationships, never canonical person identities or exclusive account ownership.</summary>
public enum ActorReferenceRelationship
{
    ExplicitAlias,
    JointRoles,
    DistinctActors,
}

public enum ActorProfileRelationship
{
    AttributedProfile,
    CreditedProjectProfile,
    ExplicitOwnerStatement,
}

public enum ActorContextKind
{
    IndividualSpeakerStatement,
    ExplicitPersonDescription,
    ExplicitGroupDescription,
    GroupMemberStatement,
    NamedRoleContext,
}

public enum ActorFactKind
{
    Aliases,
    Profiles,
    Contacts,
    Context,
    Kind,
    Relationships,
}

/// <summary>
/// These models are shared by the local parser and retained-input mapper. TEvidence is either
/// NormalizationTextEvidence or NormalizedEvidenceReference; mapping changes coordinates, not meaning.
/// Actor/reference UUIDs are local to the cited account and extractor version, not global IDs.
/// </summary>
public sealed record CreditedActorFacts<TEvidence>(
    string Version,
    Guid ActorUuid,
    Guid ReferenceUuid,
    TEvidence Anchor,
    IReadOnlyList<ActorAliasFact<TEvidence>> Aliases,
    IReadOnlyList<ActorProfileFact<TEvidence>> Profiles,
    IReadOnlyList<ActorContactFact<TEvidence>> Contacts,
    IReadOnlyList<ActorContextFact<TEvidence>> Context,
    ActorKindFact<TEvidence>? Kind,
    IReadOnlyList<ActorCoReferenceFact<TEvidence>> Relationships,
    IReadOnlyList<ActorEvidenceCoverage<TEvidence>> Coverage,
    IReadOnlyList<ActorEvidenceUnresolved<TEvidence>> Unresolved
)
{
    /// <summary>Enumerates original citations, including coverage and unresolved attachment evidence.</summary>
    public IEnumerable<TEvidence> Citations() =>
        new[] { Anchor }
            .Concat(Aliases.SelectMany(fact => fact.Evidence))
            .Concat(Profiles.SelectMany(fact => fact.Evidence))
            .Concat(Contacts.SelectMany(fact => fact.Evidence))
            .Concat(Context.SelectMany(fact => fact.Evidence))
            .Concat(Kind?.Evidence ?? [])
            .Concat(Relationships.SelectMany(fact => fact.Evidence))
            .Concat(Coverage.SelectMany(fact => fact.Fields))
            .Concat(Unresolved.SelectMany(fact => fact.Evidence));
}

public sealed record ActorAliasFact<TEvidence>(
    Guid Uuid,
    Guid ReferenceUuid,
    string Wording,
    IReadOnlyList<TEvidence> Evidence
);

/// <summary>A qualified profile lookup is a source assertion, not a verified entity identifier.</summary>
public sealed record ActorProfileFact<TEvidence>(
    Guid Uuid,
    string Literal,
    string Provider,
    string Namespace,
    string LookupKey,
    ActorProfileRelationship Relationship,
    IReadOnlyList<TEvidence> Evidence
);

/// <summary>Contact keys are nonunique hints. The original literal and attachment evidence remain separate.</summary>
public sealed record ActorContactFact<TEvidence>(
    Guid Uuid,
    string Literal,
    string ContactType,
    string LookupKey,
    IReadOnlyList<TEvidence> Evidence
);

public sealed record ActorContextFact<TEvidence>(
    Guid Uuid,
    ActorContextKind ContextKind,
    IReadOnlyList<TEvidence> Evidence
);

/// <summary>Explicit expressed-actor kind only. Name spelling, contact equality and a profile do not supply it.</summary>
public sealed record ActorKindFact<TEvidence>(string Kind, IReadOnlyList<TEvidence> Evidence);

/// <summary>
/// Alias endpoints name source references. JointRoles may have one reference filling several
/// roles. DistinctActors preserves a coordinated list; it is not evidence of global inequality.
/// </summary>
public sealed record ActorCoReferenceFact<TEvidence>(
    Guid Uuid,
    ActorReferenceRelationship Relationship,
    IReadOnlyList<Guid> ReferenceUuids,
    IReadOnlyList<RecordingCreditRole> Roles,
    IReadOnlyList<TEvidence> Evidence
);

/// <summary>Missing or nonexhaustive coverage cannot turn an empty extracted list into negative evidence.</summary>
public sealed record ActorEvidenceCoverage<TEvidence>(
    ActorFactKind Kind,
    bool Complete,
    string Reason,
    IReadOnlyList<TEvidence> Fields
);

/// <summary>An unattributed contact/profile remains here, rather than being copied onto every nearby actor.</summary>
public sealed record ActorEvidenceUnresolved<TEvidence>(
    string Reason,
    IReadOnlyList<Guid> CandidateReferenceUuids,
    IReadOnlyList<TEvidence> Evidence
);
