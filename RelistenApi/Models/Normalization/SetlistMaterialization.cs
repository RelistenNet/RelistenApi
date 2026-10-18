using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

public sealed record SetlistMaterializationRequest(Guid RepertoireResolutionAttemptUuid);

public sealed record SetlistMaterializationRegistration(
    Guid RegistrationUuid,
    Guid ProcessingRegistrationUuid,
    Guid? CurrentAttemptUuid,
    Guid? EditionUuid = null,
    bool ObservationRetired = false
);

/// <summary>The complete current source and accepted image are read together; no caller supplies these facts.</summary>
public sealed record SetlistMaterializationContext
{
    public required CompletedRepertoireConsumption Consumption { get; init; }
    public required Guid EditionUuid { get; init; }
    public required Guid StructureUuid { get; init; }
    public required NormalizationSubjectKey Owner { get; init; }
    public required MusicMaterializationOwnerContext OwnerContext { get; init; }
    public required RepertoireResolutionReadSet ReadSet { get; init; }
    public required RepertoireResolutionReadSet OwnReadSet { get; init; }
    public required IReadOnlyList<NormalizationBindingInputReference> InputReferences { get; init; }
    public required MusicMaterializationPolicyState Policy { get; init; }
    public MusicRecord<SetlistEditionChange>? Edition { get; init; }
    public MusicRecord<SetlistStructureChange>? Structure { get; init; }
    public IReadOnlyList<SetlistMaterializationSetState> Sets { get; init; } = [];
    public IReadOnlyList<MusicRecord<SongOccurrenceChange>> Occurrences { get; init; } = [];
    public IReadOnlyList<SetlistAuthorizationRecord> Authorizations { get; init; } = [];
    public IReadOnlyList<SetlistMaterializationCorrespondence> Correspondences { get; init; } = [];
    public IReadOnlyList<SetlistStructureSource> Sources { get; init; } = [];
    public IReadOnlyList<SetlistMaterializationSourceVersion> SourceVersions { get; init; } = [];
    public IReadOnlyList<SetlistContributionRetirementRequirement> ContributionsToRetire { get; init; } =
    [];
    public IReadOnlyList<string> ReviewedClaimExclusions { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];
}

public sealed record SetlistMaterializationSourceVersion(
    Guid ProcessingAttemptUuid,
    string ProcessingVersion
);

public sealed record SetlistMaterializationSetState(SetlistStructureSet Value, long Revision);

/// <summary>Latest matching history is selected before reuse; removed or superseded targets remain visible.</summary>
public sealed record SetlistMaterializationCorrespondence(
    long StructureRevision,
    Guid ProcessingAttemptUuid,
    string ProcessingVersion,
    Guid InputRevisionUuid,
    int AccountIndex,
    SetlistClaimReference Claim,
    Guid TargetUuid,
    NormalizationResourceKey? ProviderIdentity,
    Guid? AuthorizationUuid
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SetlistClaimMaterializationDecision), "claim")]
[JsonDerivedType(typeof(SetlistRemovalMaterializationDecision), "removal")]
[JsonDerivedType(typeof(SetlistAccountMaterializationDecision), "account")]
public abstract record SetlistMaterializationDecision(
    Guid DecisionUuid,
    string Key,
    MusicMaterializationDecisionDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    MusicUsageCorrespondenceDecision? Evaluation
);

public sealed record SetlistClaimMaterializationDecision(
    Guid DecisionUuid,
    string Key,
    MusicMaterializationDecisionDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    SetlistStructureSource Source,
    SetlistClaimReference Claim,
    IReadOnlyList<SetlistTargetRevision> Targets,
    Guid? ExistingAuthorizationUuid = null,
    MusicUsageCorrespondenceDecision? Evaluation = null
) : SetlistMaterializationDecision(DecisionUuid, Key, Disposition, Reason, Evidence, Evaluation);

public sealed record SetlistRemovalMaterializationDecision(
    Guid DecisionUuid,
    string Key,
    MusicMaterializationDecisionDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    SetlistRemovalKind RemovalKind,
    SetlistTargetRevision Target,
    IReadOnlyList<SetlistStructureSource> Sources,
    Guid? ExistingAuthorizationUuid = null,
    MusicUsageCorrespondenceDecision? Evaluation = null
) : SetlistMaterializationDecision(DecisionUuid, Key, Disposition, Reason, Evidence, Evaluation);

public sealed record SetlistAccountMaterializationDecision(
    Guid DecisionUuid,
    string Key,
    MusicMaterializationDecisionDisposition Disposition,
    string Reason,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    SetlistStructureSource Source
) : SetlistMaterializationDecision(DecisionUuid, Key, Disposition, Reason, Evidence, null);

public sealed record SetlistMaterializationPreparation(
    SetlistMaterializationContext? Context,
    SetlistStructureChange? Image,
    IReadOnlyList<SetlistMaterializationDecision> Decisions,
    bool ContextOmitted = false,
    int? TotalDecisions = null,
    bool RequiresObservationRetirement = false
)
{
    public IReadOnlyList<MusicUsageCorrespondenceContext> CorrespondenceContexts { get; init; } =
    [];
    public IReadOnlyList<SetlistContributionRetirementRequirement> ContributionsToRetire { get; init; } =
    [];
}

/// <summary>Current owned membership for CAS, with the original actual installing admission for semantic ownership.</summary>
public sealed record SetlistContributionRetirementRequirement(
    NormalizationSubjectKey Owner,
    NormalizationResourceKey Contributor,
    long ContributionRevision,
    Guid ObservedWorkUuid,
    NormalizationWorkTicket ObservedTicket,
    long InstalledContributionRevision,
    NormalizationInputContributionState ObservedContribution
);

public sealed record SetlistCompletedContributionRetirement(
    NormalizationSubjectKey Owner,
    NormalizationResourceKey Contributor,
    long ObservedContributionRevision,
    Guid WorkUuid,
    NormalizationPublicationResult? Receipt,
    bool Superseded
);

/// <summary>Releases one obsolete private contribution after its replacement owner has acknowledged the source.</summary>
public sealed record SetlistMaterializationRetirement(
    Guid WorkUuid,
    NormalizationSubjectKey Owner,
    NormalizationResourceKey Contributor,
    long ExpectedContributionRevision,
    Guid AdoptedWorkUuid,
    MusicMaterializationOwnerContext OwnerContext,
    RepertoireResolutionReadSet ReadSet,
    NormalizationWorkTicket? Ticket = null,
    NormalizationPublicationResult? Receipt = null,
    string? FailureReason = null,
    bool Superseded = false
);

public sealed record SetlistMaterializationAttempt(
    Guid AttemptUuid,
    SetlistMaterializationRegistration Registration,
    SetlistMaterializationRequest? Request,
    string ProcessingVersion,
    MusicMaterializationRetrySelection? RetrySelection = null,
    Guid? SelectedRepertoireAttemptUuid = null,
    SetlistMaterializationPreparation? Preparation = null,
    NormalizationWorkTicket? Ticket = null,
    NormalizationPublicationResult? Receipt = null,
    SetlistMaterializationRetirement? Retirement = null,
    MusicMaterializationResult? Result = null,
    long CheckpointRevision = 0,
    string? AdmissionFailureReason = null
)
{
    /// <summary>The exact prepared edition, retained in compact reads; null before preparation or for an unbound observer.</summary>
    public Guid? TargetEditionUuid { get; init; }

    public IReadOnlyList<SetlistCompletedContributionRetirement> CompletedRetirements { get; init; } =
    [];
    public MusicMaterializationRetryRequirement? CleanupRetryRequirement { get; init; }
}

public sealed record SetlistMaterializationDecisionPage(
    IReadOnlyList<SetlistMaterializationDecision> Decisions,
    Guid? NextCursor
);

public sealed record SetlistMaterializationRegistrationPage(
    IReadOnlyList<SetlistMaterializationRegistration> Registrations,
    Guid? NextCursor
);

public sealed record SetlistMaterializationRecoveryPage(
    IReadOnlyList<SetlistMaterializationAttempt> Attempts,
    Guid? NextCursor
);
