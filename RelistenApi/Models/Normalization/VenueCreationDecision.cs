using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>An explicitly selected, current terminal parent site and the revision reviewed by the operator.</summary>
public sealed record VenueCreationParent(Guid VenueUuid, long ExpectedRevision);

/// <summary>
/// Creates a first physical identity under an already registered venues/venue owner. Kind is
/// required: an imported label does not establish site versus room. Names must include an
/// evidenced display name. All assertion UUIDs are caller-stable and belong to VenueUuid.
/// Evidence resources must be configured on this owner; historical revisions remain valid.
/// Creation records an accept operation; later metadata corrections use their existing history.
/// </summary>
public sealed record VenueCreationDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid VenueUuid,
    string DisplayName,
    string Kind,
    NormalizedEvidenceReference IdentityEvidence,
    IReadOnlyList<VenueNameAssertion> Names,
    IReadOnlyList<VenueLocationAssertion> Locations,
    IReadOnlyList<VenueExternalIdentity> ExternalIdentities,
    string Reason,
    VenueCreationParent? Parent = null
);
