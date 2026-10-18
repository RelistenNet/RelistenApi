using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public enum SetlistAuthorizationAction
{
    Authorize,
    Block,
    Revoke,
}

public enum SetlistRemovalKind
{
    Set,
    Occurrence,
    Edition,
}

/// <summary>A set index or passage proposal UUID within one exact retained account, never an accepted identity.</summary>
public sealed record SetlistClaimReference(int? SetIndex = null, Guid? ProposalUuid = null);

/// <summary>Sets use their current structure revision; occurrences and editions use their own metadata revision. Zero declares a new target.</summary>
public sealed record SetlistTargetRevision(Guid Uuid, long ExpectedRevision);

public sealed record SetlistCorrespondenceApproval(
    Guid AuthorizationUuid,
    SetlistStructureSource Source,
    SetlistClaimReference Claim,
    SetlistAuthorizationAction Action,
    IReadOnlyList<SetlistTargetRevision> Targets,
    Guid? ExpectedPriorAuthorizationUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

/// <summary>Approval names one removal at its exact current revision; it cannot retire a later reactivation.</summary>
public sealed record SetlistRemovalApproval(
    Guid AuthorizationUuid,
    SetlistRemovalKind Kind,
    SetlistTargetRevision Target,
    SetlistAuthorizationAction Action,
    IReadOnlyList<SetlistStructureSource> Sources,
    Guid? ExpectedPriorAuthorizationUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

/// <summary>
/// Reviews historical source interpretation only. It neither asserts current source clocks nor
/// changes accepted metadata. Publication must independently consume the original proposal read set.
/// </summary>
public sealed record SetlistCorrespondenceDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    Guid EditionUuid,
    long ExpectedEditorialRevision,
    long ExpectedStructureRevision,
    long ExpectedEditionRevision,
    IReadOnlyList<SetlistCorrespondenceApproval> Correspondence,
    IReadOnlyList<SetlistRemovalApproval> Removals,
    string Reason
);

public sealed record SetlistRemovalAuthorization(
    SetlistRemovalKind Kind,
    Guid TargetUuid,
    Guid AuthorizationUuid
);

/// <summary>Immutable event and original historical tickets; Active is the current head, including blocks and revocations.</summary>
public sealed record SetlistAuthorizationRecord(
    Guid AuthorizationUuid,
    Guid EditionUuid,
    Guid OperationUuid,
    string Kind,
    SetlistAuthorizationAction Action,
    SetlistCorrespondenceApproval? Correspondence,
    SetlistRemovalApproval? Removal,
    IReadOnlyList<NormalizationWorkTicket> OriginalSourceTickets,
    bool Active,
    DateTime CreatedAt
);
