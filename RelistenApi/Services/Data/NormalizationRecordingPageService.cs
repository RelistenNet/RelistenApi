using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Runs one saved, bounded catalog selection. Each recording owns its attempt and transactions;
/// this runner holds no database lock while processing another recording.
/// </summary>
public sealed class NormalizationRecordingPageService(
    NormalizationRecordingPageStore pages,
    NormalizationRecordingProcessor processor,
    NormalizationRecordingProposalService proposals
)
{
    public const int DefaultLimit = 25;

    /// <summary>
    /// Creates or resumes an immutable selection and its stable recording attempts. Reuse the
    /// page UUID and selection arguments after a retryable failure or cancellation. Terminal
    /// attempts replay without refreshing evidence. Artist membership is a selection-time filter;
    /// each processor validates the recording's current facts and ownership.
    /// </summary>
    /// <remarks>
    /// Retryable preparation/database failures leave their item unfinished and processing continues.
    /// Cancellation propagates without a failure receipt. Unexpected errors receive a bounded
    /// nonretryable receipt and propagate. A continuation is available only when all items settle;
    /// Missing, Unresolved and Stale count as attention outcomes, not successful normalization.
    /// </remarks>
    public async Task<NormalizationRecordingPageResult> ProcessPageAsync(
        Guid pageUuid,
        Guid? artistUuid = null,
        Guid? afterSourceUuid = null,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        var page = await pages.GetOrCreateAsync(
            pageUuid,
            artistUuid,
            afterSourceUuid,
            limit,
            NormalizationRecordingProcessor.Version,
            cancellationToken
        );
        var invocationUuid = Guid.NewGuid();
        var results = new List<NormalizationRecordingPageItemResult>(page.Items.Count);
        foreach (var item in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ProcessItemAsync(page, item, invocationUuid, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(page, invocationUuid, results);
    }

    private async Task<NormalizationRecordingPageItemResult> ProcessItemAsync(
        NormalizationRecordingPage page,
        NormalizationRecordingPageItem item,
        Guid invocationUuid,
        CancellationToken ct
    )
    {
        try
        {
            await RequireAttemptHeaderAsync(page, item, ct);
            var result = await processor.ProcessAsync(item.AttemptUuid, item.SourceUuid, ct);
            return new(item.SourceUuid, item.AttemptUuid, result, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            ct.ThrowIfCancellationRequested();
            var failure = ClassifyFailure(error);
            // An incompatible attempt can appear between the initial header read and the
            // processor's insert. It is an invariant failure, not a preparation to retry forever.
            if (
                error is NormalizationConflictException
                && await HasConflictingHeaderAsync(page, item, ct)
            )
            {
                failure = (NormalizationRecordingPageFailureCode.ProcessingFailed, false);
            }
            var receipt = await pages.RecordFailureAsync(
                invocationUuid,
                page.PageUuid,
                item.AttemptUuid,
                failure.Code,
                failure.Retryable,
                ct
            );
            if (!failure.Retryable)
            {
                throw;
            }
            return new(item.SourceUuid, item.AttemptUuid, null, receipt);
        }
    }

    private async Task RequireAttemptHeaderAsync(
        NormalizationRecordingPage page,
        NormalizationRecordingPageItem item,
        CancellationToken ct
    )
    {
        if (await HasConflictingHeaderAsync(page, item, ct))
        {
            throw new InvalidOperationException(
                "A planned recording attempt has an incompatible saved request."
            );
        }
    }

    private async Task<bool> HasConflictingHeaderAsync(
        NormalizationRecordingPage page,
        NormalizationRecordingPageItem item,
        CancellationToken ct
    )
    {
        var attempt = await proposals.GetAttemptAsync(item.AttemptUuid, ct);
        return attempt != null
            && (
                attempt.SourceUuid != item.SourceUuid
                || attempt.ProcessingVersion != page.ProcessingVersion
            );
    }

    private static (NormalizationRecordingPageFailureCode Code, bool Retryable) ClassifyFailure(
        Exception error
    )
    {
        if (error is NormalizationConflictException or NormalizationStaleInputException)
        {
            return (NormalizationRecordingPageFailureCode.PreparationConflict, true);
        }
        // DbService wraps PostgreSQL and timeout errors. Inspect typed causes without retaining
        // their messages, SQL or provider content in the durable page receipt.
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            if (cause is TimeoutException)
            {
                return (NormalizationRecordingPageFailureCode.ProcessingTimeout, true);
            }
            if (cause is NpgsqlException { IsTransient: true })
            {
                return (NormalizationRecordingPageFailureCode.DatabaseFailed, true);
            }
        }
        return (NormalizationRecordingPageFailureCode.ProcessingFailed, false);
    }
}
