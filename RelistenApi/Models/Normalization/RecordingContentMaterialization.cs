using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public sealed record RecordingContentMaterializationRequest(Guid RepertoireResolutionAttemptUuid);

public sealed record RecordingContentMaterializationRegistration(
    Guid RegistrationUuid,
    Guid SourceUuid,
    Guid? CurrentAttemptUuid
);

/// <summary>Coordinates identify retained file/title claims; none is an accepted content or occurrence UUID.</summary>
public sealed record RecordingContentSourceReference(
    Guid ExtractionAttemptUuid,
    Guid InputRevisionUuid,
    Guid ProposalUuid,
    Guid TrackUuid,
    int SegmentPosition,
    ProviderObservationOrigin FileOrigin,
    Guid ClaimUuid,
    string CorrespondenceKey
);

public sealed record RecordingContentCorrespondence(
    Guid ContentUuid,
    long ExpectedRevision,
    RecordingContentSourceReference Source,
    Guid? AuthorizationUuid = null
);

public sealed record RecordingContentWithdrawal(
    Guid ContentUuid,
    long ExpectedRevision,
    Guid AuthorizationUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

public sealed record RecordingContentImage(
    Guid SourceUuid,
    IReadOnlyList<TrackContentChange> Contents,
    IReadOnlyList<RecordingContentCorrespondence> Correspondence,
    IReadOnlyList<RecordingContentWithdrawal> Withdrawals
);

public sealed record RecordingContentAcceptedRow(
    MusicRecord<TrackContentChange> Record,
    Guid ArtistUuid,
    Guid SourceUuid,
    bool Managed
);

public sealed record RecordingContentMaterializationContext
{
    public required CompletedRepertoireConsumption Consumption { get; init; }
    public required Guid SourceUuid { get; init; }
    public required Guid ArtistUuid { get; init; }
    public required MusicMaterializationOwnerContext OwnerContext { get; init; }
    public required RepertoireResolutionReadSet ReadSet { get; init; }
    public required IReadOnlyList<NormalizationBindingInputReference> InputReferences { get; init; }
    public required MusicMaterializationPolicyState Policy { get; init; }
    public IReadOnlyList<RecordingContentAcceptedRow> Contents { get; init; } = [];
    public IReadOnlyList<RecordingContentCorrespondence> PriorCorrespondence { get; init; } = [];
    public IReadOnlyList<MusicUsageSourceImage> PriorSourceImages { get; init; } = [];
    public IReadOnlyList<MusicUsageKnownCorrespondence> KnownCorrespondences { get; init; } = [];
    public IReadOnlyList<RecordingContentAuthorizationRecord> Authorizations { get; init; } = [];
    public IReadOnlyList<string> ReviewedContentExclusions { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];
}

public sealed record RecordingContentMaterializationDecision(
    Guid DecisionUuid,
    string Key,
    MusicMaterializationDecisionDisposition Disposition,
    string Reason,
    RecordingContentSourceReference? Source,
    Guid? ContentUuid,
    long ExpectedRevision,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    bool Withdrawal = false,
    Guid? ExistingAuthorizationUuid = null,
    MusicUsageCorrespondenceDecision? Evaluation = null
);

public sealed record RecordingContentMaterializationPreparation(
    RecordingContentMaterializationContext? Context,
    RecordingContentImage? Image,
    IReadOnlyList<RecordingContentMaterializationDecision> Decisions,
    bool ContextOmitted = false,
    int? TotalDecisions = null
)
{
    public IReadOnlyList<MusicUsageCorrespondenceContext> CorrespondenceContexts { get; init; } =
    [];
}

public sealed record RecordingContentMaterializationAttempt(
    Guid AttemptUuid,
    RecordingContentMaterializationRegistration Registration,
    RecordingContentMaterializationRequest? Request,
    string ProcessingVersion,
    MusicMaterializationRetrySelection? RetrySelection = null,
    Guid? SelectedRepertoireAttemptUuid = null,
    RecordingContentMaterializationPreparation? Preparation = null,
    NormalizationWorkTicket? Ticket = null,
    NormalizationPublicationResult? Receipt = null,
    MusicMaterializationResult? Result = null,
    long CheckpointRevision = 0,
    string? AdmissionFailureReason = null
);

public sealed record RecordingContentAuthorizationRecord(
    Guid AuthorizationUuid,
    Guid SourceUuid,
    RecordingContentSourceReference? Source,
    IReadOnlyList<SetlistTargetRevision> Targets,
    SetlistAuthorizationAction Action,
    bool Withdrawal,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    Guid? OperationUuid = null,
    Guid? ProcessorDecisionUuid = null,
    bool Active = true
);

public sealed record RecordingContentMaterializationDecisionPage(
    IReadOnlyList<RecordingContentMaterializationDecision> Decisions,
    Guid? NextCursor
);

public sealed record RecordingContentMaterializationRegistrationPage(
    IReadOnlyList<RecordingContentMaterializationRegistration> Registrations,
    Guid? NextCursor
);

public sealed record RecordingContentMaterializationRecoveryPage(
    IReadOnlyList<RecordingContentMaterializationAttempt> Attempts,
    Guid? NextCursor
);

/// <summary>Historical-exact correspondence approval changes authority, not accepted content metadata.</summary>
public sealed record RecordingContentCorrespondenceApproval(
    Guid AuthorizationUuid,
    RecordingContentSourceReference? Source,
    IReadOnlyList<SetlistTargetRevision> Targets,
    SetlistAuthorizationAction Action,
    bool Withdrawal,
    Guid? ExpectedPriorAuthorizationUuid,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

public sealed record RecordingContentCorrespondenceReview(
    Guid OperationUuid,
    Guid SubjectUuid,
    Guid SourceUuid,
    long ExpectedEditorialRevision,
    IReadOnlyList<RecordingContentCorrespondenceApproval> Approvals,
    string Reason
);
