using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Salvages individually supported contextual proposals after a sealed local experiment.
/// Reads retained failed envelopes and writes a separate result artifact. It sends no requests,
/// changes no cache entries, and leaves both the original run and its spend ledger unchanged.
/// </summary>
public sealed class PeopleContextRecovery(DbService? database = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Recovers only invalid-response attempts belonging to this exact sealed run and job.
    /// Cancellation can leave a complete result prefix; unrecorded batches remain pending.
    /// Every output row retains the full original batch plus its failed-attempt locator.
    /// Individual decisions pass the same exact source-support checks as primary responses.
    /// </summary>
    public async Task<PeopleContextRecoveryReport> RunAsync(
        string originalRun,
        string outputDirectory,
        Guid jobUuid,
        CancellationToken cancellationToken = default
    )
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new recovery output directory.");
        }
        var manifestPath = Path.Combine(originalRun, "manifest.json");
        var original =
            JsonSerializer.Deserialize<PeopleContextBatchReport>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                Json
            ) ?? throw new ArgumentException("The original run has no valid manifest.");
        if (
            jobUuid == Guid.Empty
            || original.JobUuid != jobUuid
            || original.State is not ("completed" or "completed_partial" or "cancelled")
            || original.Method != LunaPeopleContextMatcher.Version
        )
        {
            throw new ArgumentException(
                "Recover a sealed contextual matching run for the exact job and method."
            );
        }
        var inputPath = Path.Combine(originalRun, "inputs.jsonl");
        var resultPath = Path.Combine(originalRun, "results.jsonl");
        if (
            await FileHashAsync(inputPath) != original.InputSha256
            || await FileHashAsync(resultPath) != original.ResultsSha256
        )
        {
            throw new IOException(
                "The original input or results differ from their completed manifest."
            );
        }
        var failedRows = await ReadFailedRowsAsync(
            inputPath,
            resultPath,
            original.InputBatches,
            original.CompletedBatches,
            cancellationToken
        );
        var db = database ?? NormalizationLocalDatabase.Create();
        var cache = new NormalizationExtractionCacheService(db);
        var before = await cache.GetJobAsync(jobUuid, cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        var counts = await WriteRecoveredRowsAsync(
            db,
            failedRows,
            originalRun,
            outputDirectory,
            jobUuid,
            cancellationToken
        );
        var after = await cache.GetJobAsync(jobUuid, CancellationToken.None);
        var report = new PeopleContextRecoveryReport(
            "completed",
            jobUuid,
            LunaPeopleContextMatcher.Version,
            Path.GetFullPath(originalRun),
            failedRows.Count,
            counts.Batches,
            counts.Decisions,
            counts.Rejected,
            0m,
            0m,
            before.CapUsd,
            before.AccountedUsd,
            after.AccountedUsd,
            await FileHashAsync(manifestPath),
            await FileHashAsync(Path.Combine(outputDirectory, "results.jsonl"))
        );
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "manifest.json"),
            JsonSerializer.Serialize(report, Json),
            CancellationToken.None
        );
        return report;
    }

    private static async Task<(int Batches, int Decisions, int Rejected)> WriteRecoveredRowsAsync(
        DbService db,
        IReadOnlyList<FailedRow> failedRows,
        string originalRun,
        string outputDirectory,
        Guid jobUuid,
        CancellationToken cancellationToken
    )
    {
        var recoveredCount = 0;
        var rejectedCount = 0;
        var batchesWithRecovered = 0;
        await using (
            var writer = new StreamWriter(
                new FileStream(Path.Combine(outputDirectory, "results.jsonl"), FileMode.CreateNew),
                new UTF8Encoding(false)
            )
        )
        {
            foreach (var row in failedRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempts = await db.WithWriteConnection(async connection =>
                    (
                        await connection.QueryAsync<FailedAttempt>(
                            new CommandDefinition(
                                """
                                SELECT a.uuid AS "Uuid",r.rendered_request AS "RenderedRequest",
                                       a.failed_response_body AS "ResponseBody",a.provider_request_id AS "ProviderRequestId"
                                FROM normalization.extraction_attempts a
                                JOIN normalization.extraction_requests r ON r.sha256=a.request_sha256
                                WHERE a.job_uuid=@jobUuid AND a.request_sha256=@CacheKey
                                  AND a.state='failed' AND a.reason='invalid_response'
                                  AND (@ProviderRequestId IS NULL OR a.provider_request_id=@ProviderRequestId)
                                """,
                                new
                                {
                                    jobUuid,
                                    row.Row.Result.CacheKey,
                                    row.Row.Result.ProviderRequestId,
                                },
                                cancellationToken: cancellationToken
                            )
                        )
                    ).ToList()
                );
                FailedAttempt? attempt = attempts.Count == 1 ? attempts[0] : null;
                PeopleContextRecoveredDecisions recovered;
                if (attempt?.ResponseBody is null)
                {
                    recovered = RejectAll(
                        row.Row.Input,
                        attempts.Count > 1
                            ? "ambiguous_failed_attempt"
                            : "retained_failure_unavailable"
                    );
                }
                else
                {
                    try
                    {
                        recovered = LunaPeopleContextMatcher.RecoverFailedBatch(
                            row.Row.Input,
                            row.Row.Result.CacheKey!,
                            attempt.RenderedRequest,
                            attempt.ResponseBody,
                            attempt.ProviderRequestId
                        );
                    }
                    catch (Exception error) when (error is JsonException or ArgumentException)
                    {
                        recovered = RejectAll(
                            row.Row.Input,
                            "retained_request_or_response_invalid"
                        );
                    }
                }
                var result = row.Row.Result with
                {
                    State = recovered.Decisions.Count > 0 ? "proposed" : "unresolved",
                    Decisions = recovered.Decisions,
                    Reason = "recovered_from_failed_batch",
                };
                var recovery = new PeopleContextRecoveryLocator(
                    Path.GetFullPath(originalRun),
                    row.PhysicalLine,
                    jobUuid,
                    row.Row.Result.CacheKey!,
                    attempt?.Uuid,
                    recovered.RejectedQuestions
                );
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(
                        new PeopleContextRecoveredRow(row.Row.Input, result, recovery),
                        Json
                    )
                );
                await writer.FlushAsync(CancellationToken.None);
                recoveredCount += recovered.Decisions.Count;
                rejectedCount += recovered.RejectedQuestions.Count;
                batchesWithRecovered += recovered.Decisions.Count > 0 ? 1 : 0;
            }
        }
        return (batchesWithRecovered, recoveredCount, rejectedCount);
    }

    private static PeopleContextRecoveredDecisions RejectAll(
        PeopleContextBatchInput input,
        string reason
    ) =>
        new(
            [],
            input
                .Questions.Select(question => new PeopleContextRejectedQuestion(
                    question.QuestionId,
                    reason
                ))
                .ToArray()
        );

    private static async Task<IReadOnlyList<FailedRow>> ReadFailedRowsAsync(
        string inputsPath,
        string resultsPath,
        int expectedInputs,
        int expectedResults,
        CancellationToken cancellationToken
    )
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = new StreamReader(inputsPath))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                var input =
                    JsonSerializer.Deserialize<PeopleContextBatchInput>(line, Json)
                    ?? throw new ArgumentException("The original input contains an invalid batch.");
                LunaPeopleContextMatcher.ValidateInput(input);
                if (!inputs.TryAdd(input.BatchId, JsonSerializer.Serialize(input, Json)))
                {
                    throw new ArgumentException("The original input repeats a batch ID.");
                }
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<FailedRow>();
        using (var reader = new StreamReader(resultsPath))
        {
            string? line;
            var physicalLine = 0;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                physicalLine++;
                var row =
                    JsonSerializer.Deserialize<PeopleContextBatchRow>(line, Json)
                    ?? throw new ArgumentException("The original results contain an invalid row.");
                if (
                    !inputs.TryGetValue(row.Input.BatchId, out var input)
                    || input != JsonSerializer.Serialize(row.Input, Json)
                    || row.Result.BatchId != row.Input.BatchId
                    || !seen.Add(row.Input.BatchId)
                )
                {
                    throw new ArgumentException(
                        "A result does not match its exact original input batch."
                    );
                }
                if (row.Result.State == "unresolved" && row.Result.Reason == "invalid_response")
                {
                    if (string.IsNullOrWhiteSpace(row.Result.CacheKey))
                    {
                        throw new ArgumentException(
                            "A failed result has no request cache locator."
                        );
                    }
                    failed.Add(new(physicalLine, row));
                }
            }
        }
        if (inputs.Count != expectedInputs || seen.Count != expectedResults)
        {
            throw new ArgumentException(
                "The sealed run must contain its recorded number of completed batches."
            );
        }
        return failed;
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private sealed record FailedRow(int PhysicalLine, PeopleContextBatchRow Row);

    private sealed record FailedAttempt(
        Guid Uuid,
        string RenderedRequest,
        byte[]? ResponseBody,
        string? ProviderRequestId
    );
}

/// <summary>Original usage is retained on Result; this recovery itself incurs no provider spend.</summary>
public sealed record PeopleContextRecoveredRow(
    PeopleContextBatchInput Input,
    PeopleContextMatchResult Result,
    PeopleContextRecoveryLocator Recovery
);

public sealed record PeopleContextRecoveryLocator(
    string OriginalRun,
    int OriginalPhysicalLine,
    Guid JobUuid,
    string CacheKey,
    Guid? FailedAttemptUuid,
    IReadOnlyList<PeopleContextRejectedQuestion> RejectedQuestions,
    string Basis = "recovered_from_failed_batch"
);

public sealed record PeopleContextRecoveryReport(
    string State,
    Guid JobUuid,
    string Method,
    string OriginalRun,
    int FailedBatches,
    int BatchesWithRecoveredDecisions,
    int RecoveredDecisions,
    int RejectedQuestions,
    decimal AccountedUsd,
    decimal KnownActualUsd,
    decimal OriginalJobCapUsd,
    decimal OriginalJobAccountedUsdBefore,
    decimal OriginalJobAccountedUsdAfter,
    string OriginalManifestSha256,
    string ResultsSha256
);
