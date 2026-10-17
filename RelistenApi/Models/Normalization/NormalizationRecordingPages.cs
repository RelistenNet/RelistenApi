using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Immutable bounded catalog selection. Artist membership and HasMoreAtSelection describe
/// selection time. UUID keysets do not establish a catalog snapshot or full backfill coverage.
/// </summary>
public sealed record NormalizationRecordingPage(
    Guid PageUuid,
    Guid? ArtistUuid,
    Guid? AfterSourceUuid,
    int Limit,
    string ProcessingVersion,
    DateTime SelectedAt,
    bool HasMoreAtSelection,
    IReadOnlyList<NormalizationRecordingPageItem> Items
);

/// <summary>A planned attempt has a stable UUID even before recording processing starts.</summary>
public sealed record NormalizationRecordingPageItem(
    int Position,
    Guid SourceUuid,
    Guid AttemptUuid
);

/// <summary>Safe failure categories; operator receipts never contain exception or provider text.</summary>
public enum NormalizationRecordingPageFailureCode
{
    PreparationConflict,
    DatabaseFailed,
    ProcessingTimeout,
    ProcessingFailed,
}

/// <summary>
/// A bounded failure receipt contains a stable code, never provider prose or exception details.
/// Reusing an invocation UUID for an item's identical failure returns its original receipt.
/// </summary>
public sealed record NormalizationRecordingPageFailure(
    long Sequence,
    Guid InvocationUuid,
    Guid PageUuid,
    Guid AttemptUuid,
    NormalizationRecordingPageFailureCode Code,
    bool Retryable,
    DateTime OccurredAt
);

/// <summary>Result is terminal; Failure leaves its attempt unfinished and the page resumable.</summary>
public sealed record NormalizationRecordingPageItemResult(
    Guid SourceUuid,
    Guid AttemptUuid,
    NormalizationRecordingProcessingResult? Result,
    NormalizationRecordingPageFailure? Failure
);

/// <summary>
/// Settled means every selected attempt has an outcome, including Missing, Unresolved and Stale.
/// Only a settled page can advance. Attention outcomes still need reconciliation; completion
/// does not mean recordings or canonical identities have been successfully normalized.
/// </summary>
public sealed record NormalizationRecordingPageResult(
    NormalizationRecordingPage Page,
    Guid InvocationUuid,
    IReadOnlyList<NormalizationRecordingPageItemResult> Items
)
{
    public bool Settled => Items.All(item => item.Result != null);
    public Guid? ContinuationCursor =>
        Settled && Page.HasMoreAtSelection ? Page.Items[^1].SourceUuid : null;
    public int Published =>
        Items.Count(item =>
            item.Result?.Disposition == NormalizationRecordingProcessingDisposition.Published
        );
    public int Missing =>
        Items.Count(item =>
            item.Result?.Disposition == NormalizationRecordingProcessingDisposition.Missing
        );
    public int Unresolved =>
        Items.Count(item =>
            item.Result?.Disposition == NormalizationRecordingProcessingDisposition.Unresolved
        );
    public int Stale =>
        Items.Count(item =>
            item.Result?.Disposition == NormalizationRecordingProcessingDisposition.Stale
        );
    public int Failed => Items.Count(item => item.Failure != null);
}
