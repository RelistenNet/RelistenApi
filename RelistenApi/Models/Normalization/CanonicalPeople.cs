using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Exact retained JSON value, optionally narrowed to a Unicode code-point text span.</summary>
public sealed record NormalizedEvidenceReference(
    Guid InputRevisionUuid,
    string JsonPointer,
    int? StartCodePoint = null,
    int? EndCodePoint = null,
    string? Quote = null
);

public sealed record CanonicalPerson(Guid Uuid, string DisplayName, string Kind = "person");

public sealed record PersonAlias(
    Guid Uuid,
    Guid PersonUuid,
    string Wording,
    NormalizedEvidenceReference Evidence
);

public sealed record PersonExternalIdentity(
    Guid PersonUuid,
    string Provider,
    string ResourceType,
    string ProviderKey,
    NormalizedEvidenceReference Evidence
);

public sealed record RecordingCredit(
    Guid Uuid,
    Guid SourceUuid,
    string Role,
    string OriginalText,
    Guid? PersonUuid,
    Guid? ScopeTrackUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    string State = "accepted",
    string Basis = "provider_assertion",
    string ReasonCode = "explicit_credit"
);

/// <summary>Publish identities first under (people, person, person UUID). Recording owners publish only credits and consume each resolved person's owner dependency.</summary>
public sealed record PeoplePublication(
    IReadOnlyList<CanonicalPerson> People,
    IReadOnlyList<PersonAlias> Aliases,
    IReadOnlyList<PersonExternalIdentity> ExternalIdentities,
    IReadOnlyList<RecordingCredit> Credits
);

/// <summary>The authenticated operator is supplied separately by the trusted command host.</summary>
public sealed record RecordingCreditDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    long ExpectedCreditRevision,
    string Action,
    RecordingCredit Credit,
    string Reason
);

/// <summary>Explicit relation lists make a split reviewable and prevent moving later corrections.</summary>
public sealed record PersonIdentityDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    string Action,
    Guid OriginalUuid,
    long ExpectedOriginalRevision,
    CanonicalPerson Target,
    long? ExpectedTargetRevision,
    IReadOnlyList<Guid> CreditUuids,
    string Reason
);

/// <summary>Undo uses the original move receipt; any later relation revision makes the entire reversal conflict.</summary>
public sealed record IdentityMergeReversal(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid OriginalMergeOperationUuid,
    Guid OriginalUuid,
    Guid SurvivorUuid,
    long ExpectedOriginalRevision,
    long ExpectedSurvivorRevision,
    string Reason
);
