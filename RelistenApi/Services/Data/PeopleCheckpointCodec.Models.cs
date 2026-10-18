using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

internal static partial class PeopleCheckpointCodec
{
    // These mappings belong only to persisted people checkpoints. Public domain DTOs remain
    // expanded; no table reference can escape a decoded checkpoint or name another DB row.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Envelope<T>(
        string Format,
        string Kind,
        T Value,
        IReadOnlyList<IReadOnlyList<CandidateActorEvidenceFact>> ActorEvidenceBlocks,
        IReadOnlyList<NormalizationSubjectObservation> OwnerObservations,
        IReadOnlyList<IReadOnlyList<int>> OwnerObservationLists,
        IReadOnlyList<StoredSnapshot> CandidateSnapshots
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredClaim(
        NormalizationPeopleResolutionClaim Value,
        int? CandidateSnapshotRef
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredSnapshot(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Value,
        int OwnerObservationListRef,
        IReadOnlyList<int?> ActorEvidenceRefs
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredCreditContext(
        RecordingCreditContext Value,
        int OwnerObservationListRef
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredInput(
        NormalizationRecordingPreparedInput OriginalInput,
        long ExpectedContributionRevision,
        IReadOnlyList<StoredClaim> Claims,
        IReadOnlyList<RetainedCreditUnresolved> UnresolvedCredits,
        IReadOnlyList<ProviderDecodeIssue> DecodeIssues
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredPreparation(
        NormalizationSubjectObservation ExtractionObservation,
        StoredCreditContext CreditContext,
        long? ExpectedConfigurationGeneration,
        IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
        IReadOnlyList<StoredInput> Inputs
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredTicket(NormalizationWorkTicket Value, int? OwnerObservationListRef);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredContribution(
        NormalizationPreparedInputContribution Value,
        int? OwnerObservationListRef
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredBatch(
        Guid AttemptUuid,
        Guid SourceUuid,
        Guid ExtractionAttemptUuid,
        string ProcessingVersion,
        StoredPreparation Preparation,
        StoredTicket Ticket
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredPlan(
        StoredBatch Proposal,
        ActivePeopleAcceptancePolicy? Policy,
        IReadOnlyList<PeopleAcceptanceClaimDecision> Decisions,
        PeopleAcceptancePolicyState? PolicyState,
        string? NegativeReason
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredStage(
        Guid WorkUuid,
        Guid? PersonUuid,
        ActivePeopleAcceptancePolicy? Policy,
        StoredPreparation Facts,
        IReadOnlyList<PeopleAcceptanceClaimDecision> Decisions,
        IReadOnlyList<StoredContribution> Contributions,
        long? ExpectedConfigurationGeneration,
        IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
        PeoplePublication? Identity,
        RecordingCreditReconciliation? Reconciliation,
        StoredTicket? Ticket,
        PeopleAcceptanceStageReceipt? Receipt,
        IReadOnlyList<PeopleAcceptanceScopeCoverage>? ScopeCoverage,
        PeopleAcceptancePolicyState? PolicyState,
        bool DecisionOnly,
        StoredTicket? PeerTicket
    );
}
