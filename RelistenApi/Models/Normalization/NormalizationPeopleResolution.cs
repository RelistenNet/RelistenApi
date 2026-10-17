using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Proposed identity action, never authority to write accepted people or credits.</summary>
public enum PeopleResolutionAction
{
    MatchExisting,
    CreateIdentity,
    Review,
    Unresolved,
}

/// <summary>
/// A set-level decision retains every comparison. Automatic acceptance requires a separately
/// verified evaluated policy; this proposal pipeline has no active policy or accepted writer.
/// </summary>
public sealed record PeopleResolutionDecision(
    PeopleResolutionAction Action,
    string Reason,
    Guid? PersonUuid = null
)
{
    public bool AutomaticAcceptanceAuthorized => false;
}

/// <summary>
/// The original credit and its checked file scope, with the entire candidate projection used
/// for comparison. A missing file association has ScopeIssue, never a widened recording scope.
/// </summary>
public sealed record NormalizationPeopleResolutionClaim(
    NormalizationRecordingCreditProposal Proposal,
    Guid ArtistUuid,
    Guid? ScopeTrackUuid,
    string? ScopeIssue,
    NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? Candidates,
    IReadOnlyList<IdentityCandidateComparison> Comparisons,
    PeopleResolutionDecision Decision
);

/// <summary>One extraction contributor, including unresolved prose that must not disappear.</summary>
public sealed record NormalizationPeopleResolutionInput(
    NormalizationRecordingPreparedInput OriginalInput,
    long ExpectedContributionRevision,
    IReadOnlyList<NormalizationPeopleResolutionClaim> Claims,
    IReadOnlyList<RetainedCreditUnresolved> UnresolvedCredits,
    IReadOnlyList<ProviderDecodeIssue> DecodeIssues
);

/// <summary>
/// Complete immutable preparation. The extraction stamp is derived from its saved publication
/// ticket, not refreshed owner metadata. Membership belongs to the separate resolution owner.
/// </summary>
public sealed record NormalizationPeopleResolutionPreparation(
    NormalizationSubjectObservation ExtractionObservation,
    RecordingCreditContext CreditContext,
    long? ExpectedConfigurationGeneration,
    IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
    IReadOnlyList<NormalizationPeopleResolutionInput> Inputs
);

public enum NormalizationPeopleResolutionDisposition
{
    Published,
    Unresolved,
    Stale,
}

public sealed record NormalizationPeopleResolutionResult(
    Guid AttemptUuid,
    Guid SourceUuid,
    NormalizationPeopleResolutionDisposition Disposition,
    string Reason,
    bool Replayed = false
);

/// <summary>Reusing an attempt UUID preserves its extraction request, preparation and terminal result.</summary>
public sealed record NormalizationPeopleResolutionAttempt(
    Guid AttemptUuid,
    Guid SourceUuid,
    Guid ExtractionAttemptUuid,
    string ProcessingVersion,
    NormalizationPeopleResolutionPreparation? Preparation,
    NormalizationPeopleResolutionResult? Result
);

/// <summary>Last successful proposal publication, not accepted matching or a current-freshness proof.</summary>
public sealed record NormalizationPeopleResolutionBatch(
    Guid AttemptUuid,
    Guid SourceUuid,
    Guid ExtractionAttemptUuid,
    string ProcessingVersion,
    NormalizationPeopleResolutionPreparation Preparation,
    NormalizationWorkTicket Ticket
);
