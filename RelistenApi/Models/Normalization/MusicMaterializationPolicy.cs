using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public enum MusicMaterializationRule
{
    QualifiedUsageAnchors,
    CheckedSingleFileClaimContinuity,
    BracketedWithdrawal,
    UniqueBoundSongWithinQualifiedSet,
}

public sealed record MusicMaterializationPolicyArtifact(
    Guid Uuid,
    string Version,
    string ContentHash,
    string SetlistProcessorVersion,
    string RecordingProcessorVersion,
    string SourceFactsVersion,
    string EvaluatorVersion,
    IReadOnlyList<MusicMaterializationRule> EnabledRules,
    bool FixtureOnly = false
);

public sealed record MusicMaterializationCapabilityEvaluation(int Cases, int Errors);

/// <summary>Trusted independent evaluation, verified by the host before registration; registration does not activate it.</summary>
public sealed record MusicMaterializationEvaluationReceipt(
    Guid Uuid,
    Guid ArtifactUuid,
    string ArtifactHash,
    string ReceiptHash,
    string DatasetHash,
    string SplitHash,
    string AdjudicationHash,
    string EvaluatorVersion,
    string IndependentEvaluator,
    string ApprovedBy,
    MusicMaterializationCapabilityEvaluation Correspondence,
    MusicMaterializationCapabilityEvaluation Withdrawal,
    bool FixtureOnly = false
);

public sealed record RegisteredMusicMaterializationPolicy(
    MusicMaterializationPolicyArtifact Artifact,
    MusicMaterializationEvaluationReceipt Evaluation
);

public sealed record ActiveMusicMaterializationPolicy(
    RegisteredMusicMaterializationPolicy Policy,
    NormalizationSubjectObservation OwnerObservation
);

public sealed record MusicMaterializationPolicyState(
    ActiveMusicMaterializationPolicy? Active,
    NormalizationSubjectObservation OwnerObservation
);

public sealed record MusicMaterializationPolicyDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    Guid? ExpectedArtifactUuid,
    Guid? ArtifactUuid,
    string Reason
);

public enum MusicUsageKind
{
    Set,
    Occurrence,
    TrackContent,
}

public enum MusicMaterializationOperation
{
    Correspondence,
    Withdrawal,
    Split,
    Alignment,
}

public enum MusicUsageDecisionDisposition
{
    Authorized,
    Review,
    Unresolved,
}

/// <summary>Exact original producer coordinates. Positions identify source assertions, never accepted music identities.</summary>
public sealed record MusicUsageClaimReference(
    Guid ProcessingAttemptUuid,
    Guid InputRevisionUuid,
    int? AccountIndex = null,
    Guid? ProposalUuid = null,
    int? SetIndex = null,
    Guid? TrackUuid = null,
    int? SegmentPosition = null
);

/// <summary>Checked source scope. FileIdentity names an actual provider file, never a song or occurrence.</summary>
public sealed record MusicUsageContainer(
    NormalizationResourceKey SourceLineage,
    NormalizationResourceKey AccountIdentity,
    Guid? TrackUuid = null,
    ObservedProviderIdentity? FileIdentity = null,
    ObservedProviderIdentity? SetIdentity = null
);

public sealed record MusicUsageClaimFact(
    MusicUsageClaimReference Claim,
    int? Position,
    string ContentKind,
    Guid? RepertoireSongUuid,
    ObservedProviderIdentity? UsageIdentity,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

/// <summary>
/// The consumer derives every claim from one complete authenticated original file/account image.
/// Publication reconstructs this projection from retained batches; a caller cannot assert completeness.
/// For a qualified-set projection, Claims contains all members of that set and CompetingClaims
/// contains every other claim in the account. Null means that whole-account projection is unavailable.
/// </summary>
public sealed record MusicUsageSourceImage(
    MusicUsageKind Kind,
    MusicUsageContainer Container,
    string ProcessorVersion,
    string SourceFactsVersion,
    IReadOnlyList<MusicUsageClaimFact> Claims,
    IReadOnlyList<CandidateFactsIssue> Issues,
    IReadOnlyList<MusicUsageClaimFact>? CompetingClaims = null
);

/// <summary>Includes managed and unmanaged rows in the checked scope; only exact prior source correspondence supplies Claim.</summary>
public sealed record MusicUsageAcceptedTarget(
    Guid Uuid,
    long Revision,
    MusicUsageClaimReference? Claim,
    int? Position,
    string ContentKind,
    Guid? RepertoireSongUuid,
    string Basis,
    bool Retracted,
    IReadOnlyList<NormalizedEvidenceReference> Evidence
);

/// <summary>Latest ledger heads, including blocks and revocations; historical approvals do not replace them.</summary>
public sealed record MusicUsageAuthorizationHead(
    Guid AuthorizationUuid,
    MusicMaterializationOperation Operation,
    MusicUsageClaimReference? Claim,
    Guid? TargetUuid,
    SetlistAuthorizationAction Action
);

public sealed record MusicUsageCorrespondenceContext(
    MusicUsageSourceImage Before,
    MusicUsageSourceImage After,
    IReadOnlyList<MusicUsageAcceptedTarget> AcceptedTargets,
    IReadOnlyList<MusicUsageAuthorizationHead> AuthorizationHeads,
    RepertoireResolutionReadSet ReadSet,
    IReadOnlyList<MusicUsageKnownCorrespondence>? KnownCorrespondences = null
);

/// <summary>An actual accepted source-to-content ledger record, authenticated by the consumer reader.</summary>
public sealed record MusicUsageKnownCorrespondence(
    MusicUsageClaimReference Claim,
    Guid TargetUuid,
    long TargetRevision,
    MusicMaterializationDecisionReference AcceptedDecision
);

public sealed record MusicUsageTargetRevision(Guid Uuid, long ExpectedRevision);

public enum MusicUsageFeature
{
    SameQualifiedSourceContainer,
    SameCheckedFile,
    CompleteSingleClaimImages,
    SameContentKind,
    SameAcceptedSongAndKind,
    SameQualifiedUsageAnchors,
    SameAcceptedContentAnchors,
    UniqueWholePoolCorrespondence,
    AnchorsNowAdjacent,
    SameQualifiedSet,
}

public enum MusicUsageContradiction
{
    UnsupportedSourceContext,
    IncompleteSourceContext,
    SourceScopeChanged,
    AmbiguousSourceClaims,
    CompetingAcceptedTargets,
    ReviewedTarget,
    BlockedOrRevokedAuthority,
    KindOrSongChanged,
    MissingUsageAnchors,
    UnsupportedOperation,
}

public sealed record MusicMaterializationPolicyReference(
    Guid ArtifactUuid,
    string ArtifactHash,
    Guid EvaluationUuid,
    string EvaluationHash
);

/// <summary>Consumer persists this decision in its own immutable preparation/decision ledger; no separate proof store exists.</summary>
public sealed record MusicUsageCorrespondenceDecision(
    MusicMaterializationOperation Operation,
    MusicUsageDecisionDisposition Disposition,
    MusicUsageClaimReference? PreviousClaim,
    MusicUsageClaimReference? CurrentClaim,
    IReadOnlyList<MusicUsageTargetRevision> Targets,
    MusicMaterializationRule? Rule,
    string Reason,
    IReadOnlyList<MusicUsageFeature> Features,
    IReadOnlyList<MusicUsageContradiction> Contradictions,
    MusicMaterializationPolicyReference? Policy
);

/// <summary>Identifiers only. The writer must load the exact immutable consumer decision, never trust this reference alone.</summary>
public sealed record MusicMaterializationDecisionReference(
    Guid ConsumerAttemptUuid,
    Guid ConsumerDecisionUuid
);
