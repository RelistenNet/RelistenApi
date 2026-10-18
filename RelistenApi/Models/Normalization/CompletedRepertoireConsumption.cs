using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>Immutable resolver coordinates authenticated against its current completed registration.</summary>
public sealed record CompletedRepertoireAttempt(
    Guid AttemptUuid,
    RepertoireResolutionRegistration Registration,
    RepertoireResolutionRequest Request,
    string ProcessingVersion,
    RepertoireResolutionResult Result
);

/// <summary>
/// Current accepted identity authority. Dependencies are the causal artist-owner stamps required
/// by the music writer. OwnerObservations retain the same factual observations for audit; they
/// are not candidate observations to pass to Work without a matching candidate facts scope.
/// </summary>
public sealed record CompletedRepertoireAcceptedReadSet(
    IReadOnlyList<NormalizationSubjectDependency> Dependencies,
    IReadOnlyList<NormalizationSubjectObservation> OwnerObservations
);

/// <summary>Stored association coordinates survive redirects; accepted payload and revision describe its current canonical target.</summary>
public sealed record CompletedRepertoireTarget(
    Guid RequestedUuid,
    Guid CanonicalUuid,
    Guid MusicRevisionUuid,
    MusicRecord<RepertoireSongChange> Accepted,
    IReadOnlyList<CandidateIdentityHeader> IdentityPath
);

/// <summary>
/// A complete repertoire/music account, read in the consumer's primary repeatable-read transaction.
/// Recording excludes optional source-actor aliases from every extraction group; those remain in
/// the immutable public producer batch. Their absence here grants no people completeness or clear.
/// Producer clocks remain original. Resolver candidate epochs remain in its audit history and
/// are deliberately absent from AcceptedReadSet. Historical accepted citations need no raw-head refresh.
/// </summary>
public sealed record CompletedRepertoireConsumption(
    CompletedRepertoireAttempt Attempt,
    RepertoireResolutionSourceSnapshot Source,
    IReadOnlyList<RepertoireResolutionAssociation> Decisions,
    NormalizationWorkTicket AcknowledgmentTicket,
    NormalizationPublicationResult AcknowledgmentReceipt,
    NormalizationSubjectDependency AcknowledgmentPublication,
    NormalizationRecordingProposalBatch? Recording,
    NormalizationSetlistProposalBatch? Setlist,
    RepertoireResolutionReadSet ProducerReadSet,
    IReadOnlyList<NormalizationBindingInputReference> InputReferences,
    CompletedRepertoireAcceptedReadSet AcceptedReadSet,
    IReadOnlyList<CompletedRepertoireTarget> Targets
);

/// <summary>The complete retained account exceeds the consumer's declared capacity profile.</summary>
public sealed class CompletedRepertoireConsumptionCapacityException(string message)
    : InvalidOperationException(message);

/// <summary>A newer acknowledgment is admitted; callers may wait without replacing the selected immutable receipt.</summary>
public sealed class CompletedRepertoireConsumptionPendingException(
    Guid attemptUuid,
    Guid ownerSubjectUuid,
    string message
) : Exception(message)
{
    public Guid AttemptUuid { get; } = attemptUuid;
    public Guid OwnerSubjectUuid { get; } = ownerSubjectUuid;
}
