using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Compares the original 100 development fields or a separately authenticated bounded cohort,
/// without sending label answers or source-wide context. The durable cache owns paid attempts;
/// keyless passes prove cache reuse and expose existing routing. No catalog relationship is written.
/// </summary>
public sealed partial class LunaCreditFieldEvaluationBatch
{
    public const string Version = "luna-credit-fields-v1";
    public const decimal CapUsd = 5m;
    public const int MaximumTotalAttempts = 200;
    private const int FieldCount = 100;
    private const int MaxFileBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly LunaRecordingCreditExtractor paid;
    private readonly LunaRecordingCreditExtractor keyless;
    private readonly NormalizationExtractionCacheService cache;
    private readonly DbService db;
    private readonly LunaCreditExtractionOptions options;
    private readonly RecordingCreditExtractor local = new();

    public LunaCreditFieldEvaluationBatch(
        HttpClient http,
        NormalizationExtractionCacheService cache,
        DbService db,
        LunaCreditExtractionOptions options,
        Func<string> apiKey
    )
    {
        this.cache = cache;
        this.db = db;
        this.options = options;
        paid = new(http, cache, options, apiKey);
        // This extractor cannot start paid work even if a requested cache entry is missing.
        keyless = new(http, cache, options, () => "");
    }

    public async Task<LunaCreditFieldEvaluationReport> RunAsync(
        string labelsPath,
        string fieldsPath,
        string receiptPath,
        string newOutput,
        Guid jobUuid,
        CancellationToken cancellationToken = default
    )
    {
        if (jobUuid == Guid.Empty)
        {
            throw new ArgumentException("A new bounded-job UUID is required.", nameof(jobUuid));
        }
        if (Directory.Exists(newOutput) || File.Exists(newOutput))
        {
            throw new IOException("Choose a new field comparison output directory.");
        }

        var selection = await ReadSelectionAsync(
            labelsPath,
            fieldsPath,
            receiptPath,
            cancellationToken
        );
        return await RunSelectionAsync(selection, newOutput, jobUuid, cancellationToken);
    }

    private async Task<LunaCreditFieldEvaluationReport> RunSelectionAsync(
        LunaCreditFieldSelection selection,
        string newOutput,
        Guid jobUuid,
        CancellationToken cancellationToken
    )
    {
        var fieldCount = selection.Cohort?.Plan.FieldCount ?? FieldCount;
        var capUsd = selection.Cohort?.Plan.CapUsd ?? CapUsd;
        var maximumAttempts = selection.Cohort?.Plan.MaximumTotalAttempts ?? MaximumTotalAttempts;

        Directory.CreateDirectory(newOutput);
        await WriteJsonAsync(
            Path.Combine(newOutput, "selection.json"),
            selection,
            cancellationToken
        );
        await cache.CreateJobAsync(jobUuid, capUsd, cancellationToken, maximumAttempts);

        var results = new List<LunaCreditFieldResult>();
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await WriteResultsAsync(
                selection,
                newOutput,
                jobUuid,
                results,
                selectedKeys,
                cancellationToken
            );
        }
        catch
        {
            // HTTP cancellation is already accounted by the cache. Export its durable ledger
            // without requiring the cancelled caller token; a completed manifest is never written.
            await WriteReceiptsAsync(
                newOutput,
                jobUuid,
                selectedKeys,
                fieldCount,
                maximumAttempts,
                CancellationToken.None
            );
            throw;
        }

        // Once provider work has finished, cancellation must not suppress its spend receipt.
        var audit = await WriteReceiptsAsync(
            newOutput,
            jobUuid,
            selectedKeys,
            fieldCount,
            maximumAttempts,
            CancellationToken.None
        );
        cancellationToken.ThrowIfCancellationRequested();
        var complete = results.All(result =>
            result.State == "no_text"
            || result.State == "not_requested_contact_scope"
            || (
                result.Luna?.State == "extracted"
                && result.CacheReplay is { State: "extracted", FromCache: true }
                && result.CurrentRouting.State is "local" or "extracted"
            )
        );
        var report = new LunaCreditFieldEvaluationReport(
            complete ? "completed" : "completed_partial",
            selection.Cohort == null ? Version : CohortVersion,
            jobUuid,
            selection,
            options,
            results.Count,
            results.Count(result => result.State == "no_text"),
            results.Count(result => result.Luna?.FromCache == true),
            results.Count(result => result.CacheReplay?.FromCache == true),
            results
                .GroupBy(result => result.State)
                .ToDictionary(group => group.Key, group => group.Count()),
            results
                .GroupBy(result => result.CurrentRouting.State)
                .ToDictionary(group => group.Key, group => group.Count()),
            audit.Job,
            audit.Attempts.Count(attempt => attempt.JobUuid == jobUuid),
            Hash(
                await File.ReadAllBytesAsync(
                    Path.Combine(newOutput, "results.jsonl"),
                    cancellationToken
                )
            ),
            Hash(
                await File.ReadAllBytesAsync(
                    Path.Combine(newOutput, "receipts.json"),
                    cancellationToken
                )
            ),
            Hash(
                await File.ReadAllBytesAsync(
                    typeof(LunaRecordingCreditExtractor).Assembly.Location,
                    cancellationToken
                )
            ),
            Hash(
                await File.ReadAllBytesAsync(
                    typeof(LunaCreditFieldEvaluationBatch).Assembly.Location,
                    cancellationToken
                )
            ),
            selection.Cohort == null ? null : CohortCoverage(selection, results, audit)
        );
        var manifest = Path.Combine(newOutput, "manifest.json");
        await WriteJsonAsync(manifest + ".writing", report, cancellationToken);
        File.Move(manifest + ".writing", manifest);
        return report;
    }

    private async Task WriteResultsAsync(
        LunaCreditFieldSelection selection,
        string output,
        Guid jobUuid,
        List<LunaCreditFieldResult> results,
        HashSet<string> selectedKeys,
        CancellationToken ct
    )
    {
        await using var writer = new StreamWriter(
            new FileStream(Path.Combine(output, "results.jsonl"), FileMode.CreateNew)
        );
        foreach (var item in selection.Items)
        {
            ct.ThrowIfCancellationRequested();
            var fields = new Dictionary<string, string?> { [item.Field] = item.Text };
            var currentLocal = local.Extract(fields);
            LunaCreditFieldResult result;
            if (selection.Cohort != null && HasContact(item.Text))
            {
                // Keep the complete field/local result in this evaluation's denominator,
                // but do not render, cache or request contact-bearing text from a provider.
                result = new(
                    item.LabelId,
                    "not_requested_contact_scope",
                    currentLocal,
                    null,
                    null,
                    new("not_evaluated", currentLocal, Reason: "contact_scope")
                );
            }
            else if (item.NoText)
            {
                result = new(
                    item.LabelId,
                    "no_text",
                    currentLocal,
                    null,
                    null,
                    new("local", currentLocal, Reason: "no_text")
                );
            }
            else
            {
                var luna = await paid.ExtractAsync(
                    jobUuid,
                    fields,
                    context: null,
                    auditLocalSuccess: true,
                    cancellationToken: ct
                );
                if (luna.CacheKey is not null)
                {
                    selectedKeys.Add(luna.CacheKey);
                }
                var replay = await keyless.ExtractAsync(
                    jobUuid,
                    fields,
                    context: null,
                    auditLocalSuccess: true,
                    cancellationToken: ct
                );
                var routing = await keyless.ExtractAsync(
                    jobUuid,
                    fields,
                    context: null,
                    auditLocalSuccess: false,
                    cancellationToken: ct
                );
                result = new(item.LabelId, luna.State, currentLocal, luna, replay, routing);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(result, Json));
            await writer.FlushAsync(ct);
            results.Add(result);
        }
    }

    /// <summary>Freezes original field bytes and references before any job or provider work begins.</summary>
    public static async Task<LunaCreditFieldSelection> ReadSelectionAsync(
        string labelsPath,
        string fieldsPath,
        string receiptPath,
        CancellationToken ct = default
    )
    {
        try
        {
            return await ReadSelectionCoreAsync(labelsPath, fieldsPath, receiptPath, ct);
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or DecoderFallbackException
            )
        {
            throw new InvalidDataException(
                "Malformed field comparison receipt, label or original field record.",
                error
            );
        }
    }

    private static async Task<LunaCreditFieldSelection> ReadSelectionCoreAsync(
        string labelsPath,
        string fieldsPath,
        string receiptPath,
        CancellationToken ct,
        LunaCreditCohortProfile? cohort = null
    )
    {
        var fieldCount = cohort?.Plan.FieldCount ?? FieldCount;
        var maximumBytes = cohort == null ? MaxFileBytes : MaximumCohortFileBytes;
        var labelsBytes = await ReadBoundedAsync(labelsPath, ct, maximumBytes);
        var fieldsBytes = await ReadBoundedAsync(fieldsPath, ct, maximumBytes);
        var receiptBytes = await ReadBoundedAsync(receiptPath, ct);
        if (cohort != null)
        {
            Require(
                Hash(labelsBytes) == cohort.Plan.Labels.Sha256
                    && Hash(fieldsBytes) == cohort.Plan.Fields.Sha256
                    && Hash(receiptBytes) == cohort.Plan.Receipt.Sha256,
                "Cohort inputs differ from the frozen job plan."
            );
        }
        using var receipt = JsonDocument.Parse(receiptBytes);
        var root = receipt.RootElement;
        if (cohort == null)
        {
            Require(
                root.GetProperty("status").GetString() == "draft_development"
                    && root.GetProperty("schema_version").GetString() == "credit-field-draft-v1"
                    && root.GetProperty("fields").GetInt32() == FieldCount,
                "Use the complete 100-field development receipt."
            );
        }
        else
        {
            RequireCohortReceipt(root, cohort);
        }
        Require(
            root.GetProperty("labels_sha256").GetString() == Hash(labelsBytes)
                && root.GetProperty("raw_fields_sha256").GetString() == Hash(fieldsBytes),
            "Input bytes differ from the approved receipt."
        );

        var labels = ReadRows(labelsBytes).ToArray();
        var fields = ReadRows(fieldsBytes).ToArray();
        Require(
            labels.Length == fieldCount && fields.Length == fieldCount,
            cohort == null
                ? "Exactly 100 complete fields are required."
                : "The complete declared cohort is required."
        );
        var byLabel = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            Require(byLabel.TryAdd(Text(label, "label_id"), label), "Duplicate label identifier.");
        }
        var items = new List<LunaCreditFieldInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var labelId = Text(field, "label_id");
            Require(
                seen.Add(labelId) && byLabel.ContainsKey(labelId),
                "Unknown or duplicate field identifier."
            );
            var label = byLabel[labelId];
            if (cohort == null)
            {
                Require(
                    Text(label, "status") == "draft_development"
                        && Text(label, "scope") == "complete_single_field"
                        && Text(label, "family_partition") == "development"
                        && Text(label, "artist_partition") == "development",
                    "Held-out or incomplete field supplied."
                );
            }
            else
            {
                RequireCohortLabel(label, cohort.Plan);
            }
            var path = Text(field, "field_path");
            const string fieldPrefix = "/raw_record/row/";
            var fieldName = path.StartsWith(fieldPrefix, StringComparison.Ordinal)
                ? path[fieldPrefix.Length..]
                : "";
            Require(
                fieldName
                    is "taper"
                        or "transferrer"
                        or "description"
                        or "source"
                        or "lineage"
                        or "taper_notes",
                "Unsupported original field path."
            );
            var text = Text(field, "field_text", allowEmpty: true);
            var sourceUuid = Guid.Parse(Text(label.GetProperty("resource"), "resource_uuid"));
            var artistUuid = Guid.Parse(Text(label.GetProperty("artist"), "uuid"));
            var evidenceId = "source_sample:" + sourceUuid.ToString("D");
            Require(
                sourceUuid != Guid.Empty
                    && artistUuid != Guid.Empty
                    && Text(label.GetProperty("resource"), "provider")
                        == "relisten_catalog_snapshot"
                    && Text(label.GetProperty("resource"), "resource_type") == "source"
                    && labelId == $"credit-field:{sourceUuid:D}:{fieldName}"
                    && Text(label, "task_id") == "extract:" + evidenceId
                    && Text(label, "evidence_id") == evidenceId
                    && Text(field, "evidence_id") == evidenceId
                    && Text(label, "field_path") == path,
                "Field/source references disagree."
            );
            var fieldHash = Hash(Encoding.UTF8.GetBytes(text));
            Require(
                Text(field, "field_sha256") == fieldHash
                    && Text(label, "field_sha256") == fieldHash
                    && label.GetProperty("field_codepoints").GetInt32()
                        == text.EnumerateRunes().Count(),
                "Original field text changed."
            );
            Require(
                cohort != null || !HasContact(text),
                "Contact-bearing text is outside this field comparison."
            );
            items.Add(
                new(
                    labelId,
                    Text(label, "task_id"),
                    evidenceId,
                    sourceUuid,
                    artistUuid,
                    fieldName,
                    text,
                    fieldHash,
                    string.IsNullOrWhiteSpace(text)
                )
            );
        }
        if (cohort != null)
        {
            RequireCohortScope(items, cohort);
        }
        var unique = items.Select(item => (item.Field, item.Text)).Distinct().Count();
        var nonemptyUnique = items
            .Where(item => !item.NoText)
            .Select(item => (item.Field, item.Text))
            .Distinct()
            .Count();
        return new(
            Hash(labelsBytes),
            Hash(fieldsBytes),
            Hash(receiptBytes),
            unique,
            nonemptyUnique,
            items,
            cohort
        );
    }

    private async Task<LunaCreditFieldAudit> WriteReceiptsAsync(
        string output,
        Guid jobUuid,
        HashSet<string> selectedKeys,
        int fieldCount,
        int maximumAttempts,
        CancellationToken ct
    )
    {
        var keys = selectedKeys.ToArray();
        var audit = await db.WithWriteConnection(async connection =>
        {
            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: ct
                )
            );
            var job = await connection.QuerySingleAsync<NormalizationExtractionJob>(
                new CommandDefinition(
                    "SELECT uuid AS \"Uuid\",cap_usd AS \"CapUsd\",accounted_usd AS \"AccountedUsd\",maximum_total_attempts AS \"MaximumTotalAttempts\" FROM normalization.extraction_jobs WHERE uuid=@jobUuid",
                    new { jobUuid },
                    transaction,
                    cancellationToken: ct
                )
            );
            var requests = (
                await connection.QueryAsync<LunaCreditFieldRequestReceipt>(
                    new CommandDefinition(
                        "SELECT sha256 AS \"CacheKey\",rendered_request AS \"RenderedRequest\",response_json AS \"ResponseJson\" FROM normalization.extraction_requests WHERE sha256=ANY(@keys) ORDER BY sha256 LIMIT @requestLimit",
                        new { keys, requestLimit = fieldCount + 1 },
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            // A cancelled HTTP attempt can stop before its cache key is returned. Include this
            // dedicated job's bounded attempts; full request bodies stay restricted to selected keys.
            var attempts = (
                await connection.QueryAsync<LunaCreditFieldAttemptReceipt>(
                    new CommandDefinition(
                        """
                        SELECT uuid AS "Uuid",request_sha256 AS "CacheKey",job_uuid AS "JobUuid",state AS "State",
                            reserved_usd AS "ReservedUsd",actual_usd AS "ActualUsd",input_tokens AS "InputTokens",
                            output_tokens AS "OutputTokens",provider_request_id AS "ProviderRequestId",reason AS "Reason",
                            failure_stage AS "FailureStage",failed_response_body AS "FailedResponseBody"
                        FROM normalization.extraction_attempts WHERE job_uuid=@jobUuid
                        ORDER BY created_at,uuid LIMIT @attemptLimit
                        """,
                        new { jobUuid, attemptLimit = maximumAttempts + 1 },
                        transaction,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            Require(
                requests.Length <= fieldCount && attempts.Length <= maximumAttempts,
                "Audit bounds exceeded; no truncated receipt is published."
            );
            transaction.Commit();
            return new LunaCreditFieldAudit(job, requests, attempts);
        });
        await WriteJsonAsync(Path.Combine(output, "receipts.json"), audit, ct);
        return audit;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken ct,
        int maximumBytes = MaxFileBytes
    )
    {
        await using var input = File.OpenRead(path);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            Require(
                output.Length + count <= maximumBytes,
                "Field comparison input exceeds its byte bound."
            );
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static IEnumerable<JsonElement> ReadRows(byte[] bytes)
    {
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static string Text(JsonElement item, string property, bool allowEmpty = false)
    {
        var element = item.GetProperty(property);
        Require(element.ValueKind == JsonValueKind.String, "Expected original string value.");
        var value = element.GetString()!;
        Require(
            allowEmpty || !string.IsNullOrWhiteSpace(value),
            "Required field reference is missing."
        );
        return value;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(output, value, Json, ct);
    }
}

public sealed record LunaCreditFieldInput(
    string LabelId,
    string TaskId,
    string EvidenceId,
    Guid SourceUuid,
    Guid ArtistUuid,
    string Field,
    string Text,
    string FieldSha256,
    bool NoText
);

public sealed record LunaCreditFieldSelection(
    string LabelsSha256,
    string FieldsSha256,
    string ReceiptSha256,
    int UniqueFieldTuples,
    int UniqueNonemptyRequestInputs,
    IReadOnlyList<LunaCreditFieldInput> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        LunaCreditCohortProfile? Cohort = null
);

public sealed record LunaCreditFieldResult(
    string LabelId,
    string State,
    RecordingCreditExtraction Local,
    LunaCreditExtractionResult? Luna,
    LunaCreditExtractionResult? CacheReplay,
    LunaCreditExtractionResult CurrentRouting
);

public sealed record LunaCreditFieldRequestReceipt(
    string CacheKey,
    string RenderedRequest,
    string? ResponseJson
);

public sealed record LunaCreditFieldAttemptReceipt(
    Guid Uuid,
    string CacheKey,
    Guid JobUuid,
    string State,
    decimal ReservedUsd,
    decimal? ActualUsd,
    long? InputTokens,
    long? OutputTokens,
    string? ProviderRequestId,
    string? Reason,
    string? FailureStage = null,
    byte[]? FailedResponseBody = null
);

public sealed record LunaCreditFieldAudit(
    NormalizationExtractionJob Job,
    IReadOnlyList<LunaCreditFieldRequestReceipt> Requests,
    IReadOnlyList<LunaCreditFieldAttemptReceipt> Attempts
);

public sealed record LunaCreditFieldEvaluationReport(
    string State,
    string Version,
    Guid JobUuid,
    LunaCreditFieldSelection Selection,
    LunaCreditExtractionOptions Options,
    int Fields,
    int NoText,
    int LunaFromCache,
    int KeylessReplayFromCache,
    IReadOnlyDictionary<string, int> States,
    IReadOnlyDictionary<string, int> RoutingStates,
    NormalizationExtractionJob Job,
    int PaidAttempts,
    string ResultsSha256,
    string ReceiptsSha256,
    string ExtractorAssemblySha256,
    string RunnerAssemblySha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        LunaCreditCohortCoverage? CohortCoverage = null
);
