using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Accepted actions and their original policy proof, separate from policy-free resolution proposals.</summary>
public sealed record PeopleAcceptancePlan(
    NormalizationPeopleResolutionBatch Proposal,
    ActivePeopleAcceptancePolicy Policy,
    IReadOnlyList<PeopleAcceptanceClaimDecision> Decisions
);

/// <summary>
/// An immutable preparation per publication. Later creations and credits reread candidate facts
/// after earlier identity writes; original extraction and proposal receipts never change.
/// Ticket is attached once after admission and is not a refreshed version of this preparation.
/// </summary>
public sealed record PeopleAcceptanceStage(
    Guid WorkUuid,
    Guid? PersonUuid,
    ActivePeopleAcceptancePolicy Policy,
    NormalizationPeopleResolutionPreparation Facts,
    IReadOnlyList<PeopleAcceptanceClaimDecision> Decisions,
    IReadOnlyList<NormalizationPreparedInputContribution> Contributions,
    long? ExpectedConfigurationGeneration,
    IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
    PeoplePublication? Identity,
    RecordingCreditReconciliation? Reconciliation,
    NormalizationWorkTicket? Ticket = null,
    PeopleAcceptanceStageReceipt? Receipt = null,
    IReadOnlyList<PeopleAcceptanceScopeCoverage>? ScopeCoverage = null
);

public sealed record PeopleAcceptanceCreditWriteOutcome(
    Guid RequestedCreditUuid,
    Guid? StoredCreditUuid,
    Guid? PersonUuid,
    string State,
    string Reason
);

/// <summary>The same transaction records accepted writes, stage outcomes and the Work watermark.</summary>
public sealed record PeopleAcceptanceStageReceipt(
    Guid WorkUuid,
    Guid? PersonUuid,
    IReadOnlyList<PeopleAcceptanceCreditWriteOutcome> Credits
);

public enum PeopleAcceptanceDisposition
{
    Applied,
    Review,
    Unresolved,
    Stale,
}

/// <summary>Partial identity progress is explicit even when later credit publication cannot proceed.</summary>
public sealed record PeopleAcceptanceResult(
    Guid AttemptUuid,
    Guid SourceUuid,
    PeopleAcceptanceDisposition Disposition,
    string Reason,
    IReadOnlyList<PeopleAcceptanceStageReceipt> Publications,
    bool Replayed = false
);

public sealed record PeopleAcceptanceApplication(
    Guid AttemptUuid,
    Guid ResolutionAttemptUuid,
    Guid SourceUuid,
    string Version,
    PeopleAcceptancePlan? Plan,
    IReadOnlyList<PeopleAcceptanceStage> Stages,
    PeopleAcceptanceResult? Result
);

/// <summary>
/// Retirement proof for a previously managed contributor scope. Missing fields and unresolved
/// prose are not explicit clearing. Citations refer to current retained fields, including nulls.
/// </summary>
public sealed record PeopleAcceptanceScopeCoverage(
    NormalizationResourceKey Contributor,
    RecordingCreditCompleteScope Scope,
    bool Complete,
    string Reason,
    IReadOnlyList<NormalizedEvidenceReference> Fields
);
