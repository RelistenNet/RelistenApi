using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Reserves possible provider cost in short transactions before HTTP. Expired or failed attempts
/// retain their maximum reservation because the provider may have processed a disconnected request.
/// A later successful response replaces only its own reservation with measured cost.
/// </summary>
public sealed class NormalizationExtractionCacheService(DbService db)
{
    public Task CreateJobAsync(
        Guid jobUuid,
        decimal capUsd = 25m,
        CancellationToken cancellationToken = default
    )
    {
        if (jobUuid == Guid.Empty || capUsd <= 0)
        {
            throw new ArgumentException("A job UUID and positive USD cap are required.");
        }

        return db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO normalization.extraction_jobs(uuid,cap_usd) VALUES(@jobUuid,@capUsd) ON CONFLICT DO NOTHING",
                    new { jobUuid, capUsd },
                    cancellationToken: cancellationToken
                )
            );
            var existing = await connection.QuerySingleAsync<decimal>(
                new CommandDefinition(
                    "SELECT cap_usd FROM normalization.extraction_jobs WHERE uuid=@jobUuid",
                    new { jobUuid },
                    cancellationToken: cancellationToken
                )
            );
            if (existing != capUsd)
            {
                throw new InvalidOperationException(
                    "Extraction job already exists with a different cap."
                );
            }
        });
    }

    public Task<NormalizationExtractionJob> GetJobAsync(
        Guid jobUuid,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<NormalizationExtractionJob>(
                new CommandDefinition(
                    "SELECT uuid AS \"Uuid\", cap_usd AS \"CapUsd\", accounted_usd AS \"AccountedUsd\" FROM normalization.extraction_jobs WHERE uuid=@jobUuid",
                    new { jobUuid },
                    cancellationToken: cancellationToken
                )
            )
        );

    public Task<string?> GetCachedResponseAsync(
        string renderedRequest,
        CancellationToken cancellationToken = default
    )
    {
        var cacheKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(renderedRequest))
        );
        return db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT response_json FROM normalization.extraction_requests WHERE sha256=@cacheKey AND rendered_request=@renderedRequest",
                    new { cacheKey, renderedRequest },
                    cancellationToken: cancellationToken
                )
            )
        );
    }

    public Task<NormalizationExtractionClaim> TryClaimAsync(
        Guid jobUuid,
        string renderedRequest,
        decimal reservationUsd,
        int maxAttempts,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderedRequest);
        if (reservationUsd <= 0 || maxAttempts < 1 || leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Positive reservation, attempt limit and lease are required."
            );
        }
        var cacheKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(renderedRequest))
        );

        return db.WithWriteConnection(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.extraction_requests(sha256,rendered_request) VALUES(@cacheKey,@renderedRequest)
                    ON CONFLICT DO NOTHING
                    """,
                    new { cacheKey, renderedRequest },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            // Every request path locks the request before the job. No lock is held across HTTP.
            var request = await connection.QuerySingleAsync<RequestRow>(
                new CommandDefinition(
                    """
                    SELECT rendered_request AS "RenderedRequest", response_json AS "ResponseJson"
                    FROM normalization.extraction_requests WHERE sha256=@cacheKey FOR UPDATE
                    """,
                    new { cacheKey },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (request.RenderedRequest != renderedRequest)
            {
                throw new InvalidOperationException("Extraction request hash collision.");
            }
            if (request.ResponseJson is not null)
            {
                transaction.Commit();
                return new NormalizationExtractionClaim(
                    "cached",
                    cacheKey,
                    ResponseJson: request.ResponseJson
                );
            }

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.extraction_attempts SET state='uncertain', reason='lease_expired', completed_at=clock_timestamp()
                    WHERE request_sha256=@cacheKey AND state='running' AND lease_until<=clock_timestamp()
                    """,
                    new { cacheKey },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            var running = await connection.QuerySingleAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.extraction_attempts WHERE request_sha256=@cacheKey AND state='running')
                    """,
                    new { cacheKey },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            var attempts = await connection.QuerySingleAsync<int>(
                new CommandDefinition(
                    "SELECT count(*)::int FROM normalization.extraction_attempts WHERE request_sha256=@cacheKey",
                    new { cacheKey },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (running || attempts >= maxAttempts)
            {
                transaction.Commit();
                return new NormalizationExtractionClaim(
                    running ? "in_progress" : "attempts_exhausted",
                    cacheKey
                );
            }

            var job = await connection.QuerySingleAsync<NormalizationExtractionJob>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid", cap_usd AS "CapUsd", accounted_usd AS "AccountedUsd"
                    FROM normalization.extraction_jobs WHERE uuid=@jobUuid FOR UPDATE
                    """,
                    new { jobUuid },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (job.AccountedUsd + reservationUsd > job.CapUsd)
            {
                transaction.Commit();
                return new NormalizationExtractionClaim("budget_exhausted", cacheKey);
            }

            var attemptUuid = Guid.NewGuid();
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.extraction_jobs SET accounted_usd=accounted_usd+@reservationUsd WHERE uuid=@jobUuid;
                    INSERT INTO normalization.extraction_attempts(uuid,request_sha256,job_uuid,state,lease_until,reserved_usd)
                    VALUES(@attemptUuid,@cacheKey,@jobUuid,'running',clock_timestamp()+@leaseDuration,@reservationUsd)
                    """,
                    new
                    {
                        attemptUuid,
                        cacheKey,
                        jobUuid,
                        leaseDuration,
                        reservationUsd,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            transaction.Commit();
            return new NormalizationExtractionClaim("claimed", cacheKey, attemptUuid);
        });
    }

    /// <summary>Commits a validated complete response before any domain publication can use it.</summary>
    public Task CompleteAsync(
        Guid attemptUuid,
        LunaCreditResponse response,
        decimal actualUsd,
        CancellationToken cancellationToken = default
    ) => FinishAsync(attemptUuid, response, actualUsd, null, cancellationToken);

    public Task FailAsync(
        Guid attemptUuid,
        string reason,
        CancellationToken cancellationToken = default,
        LunaCreditResponse? receipt = null,
        decimal? actualUsd = null,
        string? providerRequestId = null,
        LunaCreditFailureDiagnostic? diagnostic = null
    )
    {
        if (
            reason
            is not (
                "provider_error"
                or "invalid_response"
                or "cancelled"
                or "timeout"
                or "usage_exceeded_reservation"
            )
        )
        {
            throw new ArgumentException(
                "Use a classified failure reason, never upstream error text.",
                nameof(reason)
            );
        }
        return FinishAsync(
            attemptUuid,
            receipt,
            actualUsd,
            reason,
            cancellationToken,
            providerRequestId,
            FreezeDiagnostic(diagnostic)
        );
    }

    private static FailureDiagnosticSnapshot? FreezeDiagnostic(
        LunaCreditFailureDiagnostic? diagnostic
    )
    {
        if (diagnostic is null)
        {
            return null;
        }

        var stage = diagnostic.Stage switch
        {
            LunaCreditFailureStage.ResponseEnvelope => "response_envelope",
            LunaCreditFailureStage.CreditSchema => "credit_schema",
            LunaCreditFailureStage.CreditSupport => "credit_support",
            LunaCreditFailureStage.UsageLimit => "usage_limit",
            _ => throw new ArgumentException(
                "Use a supported extraction failure stage.",
                nameof(diagnostic)
            ),
        };
        if (diagnostic.ResponseBody?.Length > LunaCreditFailureDiagnostic.MaxResponseBodyBytes)
        {
            throw new ArgumentException(
                "The complete failed response exceeds the diagnostic byte limit.",
                nameof(diagnostic)
            );
        }

        // The caller may own the underlying memory. Freeze it before connection acquisition or
        // lock waits so the first terminal accounting transaction retains exactly this evidence.
        return new(stage, diagnostic.ResponseBody?.ToArray());
    }

    private Task FinishAsync(
        Guid attemptUuid,
        LunaCreditResponse? response,
        decimal? actualUsd,
        string? reason,
        CancellationToken cancellationToken,
        string? providerRequestId = null,
        FailureDiagnosticSnapshot? diagnostic = null
    ) =>
        db.WithWriteConnection(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            var cacheKey = await connection.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT request_sha256 FROM normalization.extraction_attempts WHERE uuid=@attemptUuid",
                    new { attemptUuid },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SELECT sha256 FROM normalization.extraction_requests WHERE sha256=@cacheKey FOR UPDATE",
                    new { cacheKey },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            var attempt = await connection.QuerySingleAsync<AttemptRow>(
                new CommandDefinition(
                    """
                    SELECT job_uuid AS "JobUuid",state AS "State",reserved_usd AS "ReservedUsd"
                    FROM normalization.extraction_attempts WHERE uuid=@attemptUuid FOR UPDATE
                    """,
                    new { attemptUuid },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (attempt.State is "succeeded" or "failed")
            {
                transaction.Commit();
                return;
            }

            if (response is not null)
            {
                if (
                    (reason is null && actualUsd is null)
                    || actualUsd < 0
                    || actualUsd > attempt.ReservedUsd
                    || response.InputTokens < 0
                    || response.OutputTokens < 0
                )
                {
                    throw new InvalidOperationException(
                        "Provider usage exceeds the reserved bound."
                    );
                }
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.extraction_jobs SET accounted_usd=accounted_usd-@ReservedUsd+@actualUsd WHERE uuid=@JobUuid AND @actualUsd IS NOT NULL;
                        UPDATE normalization.extraction_attempts SET state=@state,actual_usd=@actualUsd,input_tokens=@InputTokens,
                            output_tokens=@OutputTokens,provider_request_id=@ProviderRequestId,completed_at=clock_timestamp(),reason=@reason,
                            failure_stage=@FailureStage,failed_response_body=@FailedResponseBody WHERE uuid=@attemptUuid;
                        UPDATE normalization.extraction_requests SET response_json=@Json,completed_at=clock_timestamp()
                            WHERE sha256=@cacheKey AND response_json IS NULL AND @reason IS NULL
                        """,
                        new
                        {
                            attempt.JobUuid,
                            attempt.ReservedUsd,
                            actualUsd,
                            state = reason is null ? "succeeded" : "failed",
                            reason,
                            response.InputTokens,
                            response.OutputTokens,
                            response.ProviderRequestId,
                            response.Json,
                            attemptUuid,
                            cacheKey,
                            FailureStage = diagnostic?.Stage,
                            FailedResponseBody = diagnostic?.Body,
                        },
                        transaction,
                        cancellationToken: cancellationToken
                    )
                );
            }
            else
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.extraction_attempts SET state='failed',reason=@reason,provider_request_id=@providerRequestId,completed_at=clock_timestamp(),
                            failure_stage=@FailureStage,failed_response_body=@FailedResponseBody
                        WHERE uuid=@attemptUuid AND state IN ('running','uncertain')
                        """,
                        new
                        {
                            attemptUuid,
                            reason,
                            providerRequestId,
                            FailureStage = diagnostic?.Stage,
                            FailedResponseBody = diagnostic?.Body,
                        },
                        transaction,
                        cancellationToken: cancellationToken
                    )
                );
            }
            transaction.Commit();
        });

    private sealed class RequestRow
    {
        public string RenderedRequest { get; init; } = null!;
        public string? ResponseJson { get; init; }
    }

    private sealed record FailureDiagnosticSnapshot(string Stage, byte[]? Body);

    private sealed class AttemptRow
    {
        public Guid JobUuid { get; init; }
        public string State { get; init; } = null!;
        public decimal ReservedUsd { get; init; }
    }
}
