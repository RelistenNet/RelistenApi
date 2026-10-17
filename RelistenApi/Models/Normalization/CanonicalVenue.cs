using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public sealed record CanonicalVenue(
    Guid Uuid,
    string DisplayName,
    string Kind = "site",
    Guid? ParentVenueUuid = null
);

public sealed record VenueNameAssertion(
    Guid Uuid,
    Guid VenueUuid,
    string Wording,
    string? Language,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    NormalizedEvidenceReference Evidence,
    string Basis = "provider_assertion"
);

public sealed record VenueLocationAssertion(
    Guid Uuid,
    Guid VenueUuid,
    string OriginalLocality,
    string? Country,
    string? Region,
    string? City,
    string? Address,
    double? Latitude,
    double? Longitude,
    string? CoordinatePrecision,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    NormalizedEvidenceReference Evidence,
    string Basis = "provider_assertion"
);

public sealed record VenueExternalIdentity(
    Guid VenueUuid,
    string Provider,
    string ResourceType,
    string ProviderKey,
    NormalizedEvidenceReference Evidence,
    Guid? Uuid = null,
    string Basis = "provider_assertion"
);

public sealed record VenueAssignment(
    Guid Uuid,
    Guid CatalogUuid,
    Guid VenueUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    string State = "accepted",
    string Basis = "provider_assertion",
    string ReasonCode = "explicit_venue"
);

/// <summary>Publish one identity under (venues, venue, venue UUID). Imported-venue/show owners publish only assignments and consume resolved venue-owner dependencies.</summary>
public sealed record VenuePublication(
    IReadOnlyList<CanonicalVenue> Venues,
    IReadOnlyList<VenueNameAssertion> Names,
    IReadOnlyList<VenueLocationAssertion> Locations,
    IReadOnlyList<VenueExternalIdentity> ExternalIdentities,
    IReadOnlyList<VenueAssignment> ImportedVenues,
    IReadOnlyList<VenueAssignment> Shows
);

public sealed record VenueAssignmentDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    long ExpectedAssignmentRevision,
    string Action,
    bool IsShow,
    VenueAssignment Assignment,
    string Reason
);

public sealed record VenueIdentityDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    string Action,
    Guid OriginalUuid,
    long ExpectedOriginalRevision,
    CanonicalVenue Target,
    long? ExpectedTargetRevision,
    IReadOnlyList<Guid> ImportedAssignmentUuids,
    IReadOnlyList<Guid> ShowAssignmentUuids,
    string Reason
);

/// <summary>Changes one venue-owned assertion or replaces its display identity. Accept can add an assertion; other actions require its existing UUID.</summary>
public sealed record VenueMetadataDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid VenueUuid,
    long ExpectedVenueRevision,
    string Action,
    string Reason,
    VenueNameAssertion? Name = null,
    VenueLocationAssertion? Location = null,
    VenueExternalIdentity? ExternalIdentity = null,
    CanonicalVenue? Identity = null,
    NormalizedEvidenceReference? IdentityEvidence = null,
    long ExpectedAssertionRevision = 0
);
