using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Reuse both IDs after interruption. A changed policy or terminal stale result needs
/// a new attempt; changed provider evidence first needs a new lookup handoff.
/// </summary>
public sealed record NormalizationRecordingPipelineRequest(Guid AttemptUuid, Guid HandoffUuid);

public enum NormalizationRecordingPipelineDisposition
{
    Completed,
    AwaitingPolicy,
    Review,
    Stale,
    Unresolved,
}

/// <summary>
/// Compact child receipts, not accepted facts supplied by the caller. Only Completed
/// publishes the pipeline owner. Other outcomes remain pending and need their stated
/// policy, evidence or review change before a new attempt can make progress.
/// </summary>
public sealed record NormalizationRecordingPipelineResult(
    NormalizationRecordingPipelineRequest Request,
    Guid SourceUuid,
    NormalizationRecordingPipelineDisposition Disposition,
    string Reason,
    NormalizationRecordingProcessingResult Extraction,
    NormalizationPeopleResolutionResult? PeopleResolution,
    PeopleAcceptanceResult? People,
    SourceActorResolutionResult? AliasResolution,
    SourceActorAliasApplicationResult? Aliases,
    IReadOnlyList<VenueResolutionResult> Venues,
    NormalizationPublicationResult? Completion = null
);

public enum NormalizationWorkRunState
{
    Running,
    Published,
    Stale,
}

/// <summary>
/// A pending owner and its latest run. Running also includes parked domain outcomes;
/// it is not retry advice. Replay once to recover the typed outcome from durable
/// checkpoints, then wait for its required change before starting a new attempt.
/// </summary>
public sealed record NormalizationPendingWorkRun(
    NormalizationSubjectState Subject,
    NormalizationWorkTicket Ticket,
    NormalizationWorkRunState State
);

/// <summary>
/// Cursor follows the shared pending-owner scan. An empty filtered page with a cursor
/// still has more owners to inspect; each new scan also finds arrivals before it.
/// </summary>
public sealed record NormalizationPendingWorkPage(
    IReadOnlyList<NormalizationPendingWorkRun> Items,
    Guid? Next
);
