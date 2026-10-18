using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// One accepted source account, owned by its exact contributor credit support. StoredPersonUuid
/// and credit revision preserve the original acceptance; current readers resolve redirects and
/// require that the current accepted credit still identifies that same person. Actor facts remain
/// source assertions, including nonunique contact/profile hints, rather than external identities.
/// </summary>
public sealed record CandidateActorEvidenceFact(
    Guid ContextUuid,
    Guid SupportUuid,
    Guid CreditUuid,
    long CreditRevision,
    string CreditBasis,
    Guid StoredPersonUuid,
    Guid SourceUuid,
    Guid OwnerSubjectUuid,
    NormalizationResourceKey Contributor,
    Guid ExtractionAttemptUuid,
    Guid ProposalUuid,
    Guid InputRevisionUuid,
    Guid ApplicationAttemptUuid,
    Guid IntroducedWorkUuid,
    string Basis,
    string CreditedName,
    CreditedActorFacts<NormalizedEvidenceReference> Facts
);
