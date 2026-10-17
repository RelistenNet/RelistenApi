using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingPageStore
{
    private const string FailureColumns = """
        sequence AS "Sequence",invocation_uuid AS "InvocationUuid",page_uuid AS "PageUuid",
        attempt_uuid AS "AttemptUuid",code AS "Code",retryable AS "Retryable",occurred_at AS "OccurredAt"
        """;

    /// <summary>
    /// Retains a safe classified failure for one planned item. Identical invocation/attempt replay
    /// returns its original receipt; changed page, code or retryability conflicts. Failure receipts
    /// do not complete attempts or advance a page. No exception/provider text is accepted.
    /// </summary>
    public Task<NormalizationRecordingPageFailure> RecordFailureAsync(
        Guid invocationUuid,
        Guid pageUuid,
        Guid attemptUuid,
        NormalizationRecordingPageFailureCode code,
        bool retryable,
        CancellationToken ct = default
    )
    {
        RequireUuid(invocationUuid, nameof(invocationUuid));
        RequireUuid(pageUuid, nameof(pageUuid));
        RequireUuid(attemptUuid, nameof(attemptUuid));
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentException("Unknown recording page failure code.", nameof(code));
        }

        return InTransaction(
            async (connection, transaction) =>
            {
                // A page's receipt sequences must commit in order for its failure cursor. Different
                // pages remain independent; no catalog, work-owner or attempt locks are acquired.
                await LockReceipt(
                    connection,
                    transaction,
                    $"normalization:recording-page-failures:{pageUuid:D}",
                    ct
                );
                await LockReceipt(
                    connection,
                    transaction,
                    $"normalization:recording-page-failure:{invocationUuid:D}:{attemptUuid:D}",
                    ct
                );
                var previous = await connection.QuerySingleOrDefaultAsync<FailureRow>(
                    new CommandDefinition(
                        $"SELECT {FailureColumns} FROM normalization.recording_processing_page_failures WHERE invocation_uuid=@invocationUuid AND attempt_uuid=@attemptUuid",
                        new { invocationUuid, attemptUuid },
                        transaction,
                        cancellationToken: ct
                    )
                );
                if (previous != null)
                {
                    var receipt = previous.ToFailure();
                    if (
                        receipt.PageUuid != pageUuid
                        || receipt.Code != code
                        || receipt.Retryable != retryable
                    )
                    {
                        throw new NormalizationConflictException(
                            "Failure invocation already has a different receipt for this attempt."
                        );
                    }
                    return receipt;
                }

                var planned = await connection.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM normalization.recording_processing_page_items
                            WHERE page_uuid=@pageUuid AND attempt_uuid=@attemptUuid)
                        """,
                        new { pageUuid, attemptUuid },
                        transaction,
                        cancellationToken: ct
                    )
                );
                if (!planned)
                {
                    throw new ArgumentException(
                        "The attempt is not planned on this recording page.",
                        nameof(attemptUuid)
                    );
                }
                var row = await connection.QuerySingleAsync<FailureRow>(
                    new CommandDefinition(
                        $"""
                        INSERT INTO normalization.recording_processing_page_failures(invocation_uuid,page_uuid,attempt_uuid,code,retryable)
                        VALUES(@invocationUuid,@pageUuid,@attemptUuid,@failureCode,@retryable)
                        RETURNING {FailureColumns}
                        """,
                        new
                        {
                            invocationUuid,
                            pageUuid,
                            attemptUuid,
                            failureCode = code.ToString(),
                            retryable,
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
                return row.ToFailure();
            },
            ct
        );
    }

    /// <summary>
    /// Reads immutable failure receipts in sequence order. Unknown pages return an empty list.
    /// Cursor zero is the beginning; limit is 1–100. New invocations may append later failures.
    /// </summary>
    public Task<IReadOnlyList<NormalizationRecordingPageFailure>> GetFailuresAsync(
        Guid pageUuid,
        long? afterSequence = null,
        int limit = 100,
        CancellationToken ct = default
    )
    {
        RequireUuid(pageUuid, nameof(pageUuid));
        if (afterSequence is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }
        RequireLimit(limit);
        return db.WithWriteConnection<IReadOnlyList<NormalizationRecordingPageFailure>>(
            async connection => (
                    await connection.QueryAsync<FailureRow>(
                        new CommandDefinition(
                            $"""
                            SELECT {FailureColumns} FROM normalization.recording_processing_page_failures
                            WHERE page_uuid=@pageUuid AND sequence>@afterSequence ORDER BY sequence LIMIT @limit
                            """,
                            new
                            {
                                pageUuid,
                                afterSequence = afterSequence ?? 0,
                                limit,
                            },
                            cancellationToken: ct
                        )
                    )
                ).Select(row => row.ToFailure()).ToArray()
        );
    }

    private sealed record FailureRow(
        long Sequence,
        Guid InvocationUuid,
        Guid PageUuid,
        Guid AttemptUuid,
        string Code,
        bool Retryable,
        DateTime OccurredAt
    )
    {
        public NormalizationRecordingPageFailure ToFailure() =>
            new(
                Sequence,
                InvocationUuid,
                PageUuid,
                AttemptUuid,
                Enum.Parse<NormalizationRecordingPageFailureCode>(Code),
                Retryable,
                OccurredAt
            );
    }
}
