using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Authenticates a bounded, independently frozen field cohort before the shared runner can
/// create a job. Provider origins and exposure remain reviewed evidence declarations; this
/// offline boundary does not infer either from artist mappings or extraction predictions.
/// </summary>
public sealed partial class LunaCreditFieldEvaluationBatch
{
    public const string CohortVersion = "luna-credit-cohort-v1";
    public const int MaximumCohortFields = 600;
    public const decimal MaximumCohortCapUsd = 30m;
    public const int MaximumCohortAttempts = 1200;

    // The legacy three-file input remains limited to 1 MiB. A cohort can retain up to
    // 600 complete fields near the existing 200,000-byte rendered-request boundary.
    // Oversized requests remain explicit extractor outcomes; no input is shortened.
    private const int MaximumCohortFileBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions CohortJson = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Runs the entire plan's field population. The external hash authenticates the plan;
    /// the plan authenticates inputs, provenance, partitions and immutable job limits.
    /// Reuse that same plan and job UUID with a new output directory after interruption.
    /// The cache freezes money/attempt caps, but does not durably bind a job to a plan hash.
    /// </summary>
    public async Task<LunaCreditFieldEvaluationReport> RunCohortAsync(
        string planPath,
        string expectedPlanSha256,
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

        var selection = await ReadCohortSelectionAsync(
            planPath,
            expectedPlanSha256,
            cancellationToken
        );
        return await RunSelectionAsync(selection, newOutput, jobUuid, cancellationToken);
    }

    /// <summary>
    /// Verifies the exact complete cohort without database or provider access. Nondevelopment
    /// partitions require adjudicated labels; the old development-only reader is unchanged.
    /// </summary>
    public static async Task<LunaCreditFieldSelection> ReadCohortSelectionAsync(
        string planPath,
        string expectedPlanSha256,
        CancellationToken ct = default
    )
    {
        try
        {
            var planBytes = await ReadBoundedAsync(planPath, ct);
            Require(
                IsSha256(expectedPlanSha256) && Hash(planBytes) == expectedPlanSha256,
                "Cohort plan differs from its frozen external hash."
            );
            var plan = ReadClosed<LunaCreditCohortPlan>(planBytes);
            RequirePlan(plan);
            var directory = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
            var scopeBytes = await ReadBoundedAsync(ArtifactPath(directory, plan.Scope), ct);
            Require(Hash(scopeBytes) == plan.Scope.Sha256, "Cohort source scope changed.");
            var scope = ReadClosed<LunaCreditCohortScope>(scopeBytes);
            Require(
                scope.SchemaVersion == "credit-cohort-scope-v1"
                    && scope.Items.Count == plan.FieldCount
                    && scope.Items.All(item => item != null && item.ProviderOrigin != null),
                "The complete declared source scope is required."
            );
            var profile = new LunaCreditCohortProfile(expectedPlanSha256, plan, scope);
            return await ReadSelectionCoreAsync(
                ArtifactPath(directory, plan.Labels),
                ArtifactPath(directory, plan.Fields),
                ArtifactPath(directory, plan.Receipt),
                ct,
                profile
            );
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or System.Text.DecoderFallbackException
            )
        {
            throw new InvalidDataException(
                "Malformed cohort plan, receipt, label or original field record.",
                error
            );
        }
    }

    private static void RequirePlan(LunaCreditCohortPlan plan)
    {
        Require(
            plan.SchemaVersion == "luna-credit-cohort-job-v1"
                && plan.FieldCount is >= 1 and <= MaximumCohortFields
                && plan.CapUsd is > 0 and <= MaximumCohortCapUsd
                && plan.MaximumTotalAttempts is >= 1 and <= MaximumCohortAttempts,
            "Cohort limits exceed the approved bounded profile."
        );
        Require(
            plan.ArtistPartition is "development" or "validation" or "test"
                && plan.FamilyPartition is "development" or "validation" or "test",
            "Cohort partitions must be explicit."
        );
        var partitionsMatch = plan.Purpose switch
        {
            "development" => plan.ArtistPartition == "development"
                && plan.FamilyPartition == "development",
            "tune" => plan.ArtistPartition != "test"
                && plan.FamilyPartition != "test"
                && (plan.ArtistPartition == "validation" || plan.FamilyPartition == "validation"),
            "untouched_evaluation" => plan.ArtistPartition == "test"
                || plan.FamilyPartition == "test",
            _ => false,
        };
        Require(partitionsMatch, "Cohort purpose conflicts with its frozen partitions.");
        foreach (var artifact in new[] { plan.Labels, plan.Fields, plan.Receipt, plan.Scope })
        {
            Require(
                artifact != null && IsSha256(artifact.Sha256),
                "Cohort artifact hash is missing."
            );
        }
    }

    private static string ArtifactPath(string directory, LunaCreditCohortArtifact artifact)
    {
        Require(
            !string.IsNullOrWhiteSpace(artifact.Path) && !Path.IsPathRooted(artifact.Path),
            "Cohort artifacts must use plan-relative paths."
        );
        var path = Path.GetFullPath(Path.Combine(directory, artifact.Path));
        var prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        Require(
            path.StartsWith(prefix, StringComparison.Ordinal),
            "Cohort artifact escapes its plan directory."
        );
        return path;
    }

    private static T ReadClosed<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        RequireUniqueProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, CohortJson)
            ?? throw new InvalidDataException("Missing cohort object.");
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate cohort property.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                RequireUniqueProperties(value);
            }
        }
    }

    private static void RequireCohortReceipt(JsonElement receipt, LunaCreditCohortProfile cohort)
    {
        var plan = cohort.Plan;
        Require(
            receipt.GetProperty("schema_version").GetString() == "credit-field-cohort-v1"
                && receipt.GetProperty("status").GetString() == "frozen_cohort"
                && receipt.GetProperty("purpose").GetString() == plan.Purpose
                && receipt.GetProperty("fields").GetInt32() == plan.FieldCount
                && receipt.GetProperty("scope_sha256").GetString() == plan.Scope.Sha256
                && receipt.GetProperty("labels_frozen_before_predictions").GetBoolean(),
            "Use the frozen whole-cohort receipt with independent first labels."
        );
    }

    private static void RequireCohortLabel(JsonElement label, LunaCreditCohortPlan plan)
    {
        Require(
            Text(label, "scope") == "complete_single_field"
                && Text(label, "artist_partition") == plan.ArtistPartition
                && Text(label, "family_partition") == plan.FamilyPartition,
            "Label scope or partition differs from the frozen cohort."
        );
        if (plan.Purpose == "development")
        {
            Require(
                Text(label, "status") == "draft_development",
                "Development label status changed."
            );
        }
        else
        {
            Require(
                Text(label, "status") == "adjudicated"
                    && !string.IsNullOrWhiteSpace(Text(label.GetProperty("answer"), "adjudicator")),
                "Tuning and evaluation require separately adjudicated labels."
            );
        }
    }

    private static void RequireCohortScope(
        IReadOnlyList<LunaCreditFieldInput> items,
        LunaCreditCohortProfile cohort
    )
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var scope = cohort.Scope.Items[index];
            Require(
                scope.LabelId == item.LabelId
                    && scope.EvidenceId == item.EvidenceId
                    && scope.SourceUuid == item.SourceUuid
                    && scope.ArtistUuid == item.ArtistUuid
                    && scope.Field == item.Field
                    && scope.FieldSha256 == item.FieldSha256
                    && scope.FamilyPartition == cohort.Plan.FamilyPartition
                    && scope.ArtistPartition == cohort.Plan.ArtistPartition,
                "Original field differs from its ordered source/provenance scope."
            );
            var origin = scope.ProviderOrigin;
            var unknown =
                origin.Provider == "unknown"
                && origin.Kind == "unknown"
                && origin.EvidenceSha256 == null
                && origin.ResourceKeySha256 == null;
            var supported =
                origin.Provider is "archive.org" or "local" or "phish.in"
                && origin.Kind is "retained_provider_input" or "import_execution"
                && IsSha256(origin.EvidenceSha256)
                && IsSha256(origin.ResourceKeySha256);
            Require(unknown || supported, "Provider origin lacks explicit retained provenance.");
        }
    }

    private static bool HasContact(string text) =>
        Regex.IsMatch(text, @"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}.-]+\.[\p{L}]{2,}");

    private static LunaCreditCohortCoverage CohortCoverage(
        LunaCreditFieldSelection selection,
        IReadOnlyList<LunaCreditFieldResult> results,
        LunaCreditFieldAudit audit
    )
    {
        var excluded = results
            .Where(result => result.State == "not_requested_contact_scope")
            .Select(result => result.LabelId)
            .ToArray();
        var attemptedKeys = audit
            .Attempts.Select(attempt => attempt.CacheKey)
            .ToHashSet(StringComparer.Ordinal);
        return new(
            selection.Items.Count,
            selection.Items.Count(item => !item.NoText && !HasContact(item.Text)),
            results.Count(result =>
                result.Luna?.CacheKey is { } key && attemptedKeys.Contains(key)
            ),
            results.Count(result => result.Luna?.State == "extracted"),
            excluded.Length,
            selection.Items.Count(item => item.NoText),
            excluded
        );
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Plan-relative artifact whose exact bytes are authenticated before any job work.</summary>
public sealed record LunaCreditCohortArtifact(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256
);

/// <summary>Externally pinned operator plan; declares the whole task population and its frozen job limits.</summary>
public sealed record LunaCreditCohortPlan(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("field_count")] int FieldCount,
    [property: JsonPropertyName("cap_usd")] decimal CapUsd,
    [property: JsonPropertyName("maximum_total_attempts")] int MaximumTotalAttempts,
    [property: JsonPropertyName("artist_partition")] string ArtistPartition,
    [property: JsonPropertyName("family_partition")] string FamilyPartition,
    [property: JsonPropertyName("labels")] LunaCreditCohortArtifact Labels,
    [property: JsonPropertyName("fields")] LunaCreditCohortArtifact Fields,
    [property: JsonPropertyName("receipt")] LunaCreditCohortArtifact Receipt,
    [property: JsonPropertyName("scope")] LunaCreditCohortArtifact Scope
);

/// <summary>Ordered field provenance retained separately from answers and never sent to the provider.</summary>
public sealed record LunaCreditCohortScope(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("items")] IReadOnlyList<LunaCreditCohortScopeItem> Items
);

/// <summary>Exact catalog field identity, partitions and reviewed provider origin for one task.</summary>
public sealed record LunaCreditCohortScopeItem(
    [property: JsonPropertyName("label_id")] string LabelId,
    [property: JsonPropertyName("evidence_id")] string EvidenceId,
    [property: JsonPropertyName("source_uuid")] Guid SourceUuid,
    [property: JsonPropertyName("artist_uuid")] Guid ArtistUuid,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("field_sha256")] string FieldSha256,
    [property: JsonPropertyName("family_partition")] string FamilyPartition,
    [property: JsonPropertyName("artist_partition")] string ArtistPartition,
    [property: JsonPropertyName("provider_origin")] LunaCreditCohortProviderOrigin ProviderOrigin
);

/// <summary>Reviewed origin references, or explicit unknown; artist mappings alone supply no origin.</summary>
public sealed record LunaCreditCohortProviderOrigin(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("evidence_sha256")] string? EvidenceSha256,
    [property: JsonPropertyName("resource_key_sha256")] string? ResourceKeySha256
);

/// <summary>Authenticated plan and source scope retained in the cohort selection and completion report.</summary>
public sealed record LunaCreditCohortProfile(
    string PlanSha256,
    LunaCreditCohortPlan Plan,
    LunaCreditCohortScope Scope
);

/// <summary>
/// Counts all selected fields separately from model eligibility and usable predictions.
/// Requested fields have an admitted attempt in this job's full ledger, including recovery;
/// cache responses paid by another job can be available without being requested by this job.
/// </summary>
public sealed record LunaCreditCohortCoverage(
    int SelectedFields,
    int LunaEligibleFields,
    int LunaRequestedFields,
    int LunaAvailableFields,
    int ContactExcludedFields,
    int NoTextFields,
    IReadOnlyList<string> ContactExcludedLabelIds
);
