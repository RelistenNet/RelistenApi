using System;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains immutable resolution preparations and proposal checkpoints. Ordinary attempt
/// transactions never acquire Work locks; publication enters with those locks already held.
/// </summary>
public sealed partial class NormalizationPeopleResolutionStore(DbService db)
{
    public const int MaximumCheckpointBytes = PeopleCheckpointCodec.MaximumCheckpointBytes;
    private const string Columns = """
        attempt_uuid AS "AttemptUuid",source_uuid AS "SourceUuid",extraction_attempt_uuid AS "ExtractionAttemptUuid",
        processing_version AS "ProcessingVersion",preparation::text AS "PreparationJson",
        disposition AS "Disposition",reason AS "Reason"
        """;

    /// <summary>Creates an exact request against a successfully published extraction; changed headers conflict.</summary>
    public Task<NormalizationPeopleResolutionAttempt> GetOrCreateAttemptAsync(
        Guid attemptUuid,
        Guid extractionAttemptUuid,
        string processingVersion,
        CancellationToken ct = default
    )
    {
        if (attemptUuid == Guid.Empty || extractionAttemptUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Stable resolution and extraction attempt UUIDs are required."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(processingVersion);
        return InTransaction(
            async (c, t) =>
            {
                var source =
                    await c.QuerySingleOrDefaultAsync<Guid?>(
                        new CommandDefinition(
                            """
                            SELECT source_uuid FROM normalization.recording_processing_attempts
                            WHERE attempt_uuid=@extractionAttemptUuid AND disposition='published' AND batch IS NOT NULL
                            """,
                            new { extractionAttemptUuid },
                            t,
                            cancellationToken: ct
                        )
                    )
                    ?? throw new ArgumentException(
                        "People resolution requires a published recording extraction."
                    );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.people_resolution_attempts(attempt_uuid,source_uuid,extraction_attempt_uuid,processing_version)
                        VALUES(@attemptUuid,@source,@extractionAttemptUuid,@processingVersion) ON CONFLICT DO NOTHING
                        """,
                        new
                        {
                            attemptUuid,
                            source,
                            extractionAttemptUuid,
                            processingVersion,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                var row = await Require(c, t, attemptUuid, ct);
                if (
                    row.ExtractionAttemptUuid != extractionAttemptUuid
                    || row.ProcessingVersion != processingVersion
                )
                {
                    throw new NormalizationConflictException(
                        "Resolution attempt already belongs to another extraction or version."
                    );
                }
                return row.ToAttempt();
            },
            ct
        );
    }

    public Task<NormalizationPeopleResolutionAttempt?> GetAttemptAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationPeopleResolutionAttempt?>(async c =>
            (await Read((NpgsqlConnection)c, null, attemptUuid, false, ct))?.ToAttempt()
        );

    /// <summary>Reads the last successful proposal batch, even when later work is pending or stale.</summary>
    public Task<NormalizationPeopleResolutionBatch?> GetAsync(
        Guid sourceUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationPeopleResolutionBatch?>(async c =>
        {
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT a.batch::text FROM normalization.work_subjects s
                    JOIN normalization.people_resolution_attempts a ON a.attempt_uuid=s.published_work_uuid
                        AND a.source_uuid=s.subject_uuid AND a.disposition='published'
                    WHERE s.domain='people' AND s.subject_kind='recording_resolution' AND s.subject_uuid=@sourceUuid
                    """,
                    new { sourceUuid },
                    cancellationToken: ct
                )
            );
            return json == null ? null : PeopleCheckpointCodec.ReadBatch(json);
        });

    /// <summary>Returns historical computed proposals independently of current publication or terminal outcome.</summary>
    public Task<NormalizationPeopleResolutionBatch?> GetAttemptBatchAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationPeopleResolutionBatch?>(async c =>
        {
            var json = await ReadBatch((NpgsqlConnection)c, null, attemptUuid, ct);
            return json == null ? null : PeopleCheckpointCodec.ReadBatch(json);
        });

    // A complete consumer may replace its own earlier binding keys, but must not erase
    // bindings installed by another protocol under the same contributor. Configuration
    // generation identifies the union only. Contributor revisions identify the admitted
    // allocation, including untouched tombstones; no facts or owner clocks are refreshed.
    // Missing/null historical membership arrays are unsupported, not an empty initial membership.
    internal Task<NormalizationPeopleResolutionPreparation?> GetConfiguredPreparationAsync(
        Guid sourceUuid,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    ) =>
        db.WithWriteConnection<NormalizationPeopleResolutionPreparation?>(async c =>
        {
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT a.preparation::text FROM normalization.people_resolution_attempts a
                    JOIN normalization.work_runs r ON r.uuid=a.attempt_uuid
                    JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                    CROSS JOIN LATERAL (
                        SELECT CASE
                            WHEN a.preparation->>'Format'=@checkpointFormat
                                AND a.preparation->>'Kind'=@checkpointKind THEN a.preparation->'Value'
                            WHEN NOT (a.preparation ? 'Format') THEN a.preparation
                        END AS payload
                    ) AS stored
                    CROSS JOIN LATERAL (
                        SELECT CASE WHEN jsonb_typeof(stored.payload->'ExpectedContributions')='array'
                                THEN stored.payload->'ExpectedContributions' END AS prior_headers,
                            CASE WHEN jsonb_typeof(stored.payload->'Inputs')='array'
                                THEN stored.payload->'Inputs' END AS replacements
                    ) AS checkpoint
                    WHERE s.domain='people' AND s.subject_kind='recording_resolution' AND s.subject_uuid=@sourceUuid
                        AND (r.ticket->>'DesiredGeneration')::bigint=@generation
                        AND checkpoint.prior_headers IS NOT NULL AND checkpoint.replacements IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1
                            FROM jsonb_to_recordset(@contributions::jsonb)
                                AS observed("Contributor" jsonb,"Revision" bigint)
                            FULL JOIN (
                                SELECT item->'OriginalInput'->'Contributor' AS contributor,
                                    (item->>'ExpectedContributionRevision')::bigint+1 AS revision
                                FROM jsonb_array_elements(checkpoint.replacements) AS replacement(item)
                                UNION ALL
                                SELECT prior."Contributor",prior."Revision"
                                FROM jsonb_to_recordset(checkpoint.prior_headers)
                                    AS prior("Contributor" jsonb,"Revision" bigint)
                                WHERE NOT EXISTS (
                                    SELECT 1 FROM jsonb_array_elements(checkpoint.replacements) AS replacement(item)
                                    WHERE item->'OriginalInput'->'Contributor'=prior."Contributor"
                                )
                            ) AS admitted ON observed."Contributor"=admitted.contributor
                            WHERE observed."Revision" IS DISTINCT FROM admitted.revision
                        ) AND a.preparation IS NOT NULL
                    ORDER BY a.created_at DESC,a.attempt_uuid DESC LIMIT 1
                    """,
                    new
                    {
                        sourceUuid,
                        generation = membership.Owner!.DesiredGeneration,
                        checkpointFormat = PeopleCheckpointCodec.FormatVersion,
                        checkpointKind = PeopleCheckpointCodec.PreparationKind,
                        contributions = JsonSerializer.Serialize(
                            membership.Contributions.Select(
                                value => new NormalizationInputContributionRevision(
                                    value.Contributor,
                                    value.Revision
                                )
                            )
                        ),
                    },
                    cancellationToken: ct
                )
            );
            return json == null ? null : PeopleCheckpointCodec.ReadPreparation(json);
        });

    /// <summary>First preparation wins; a concurrent caller resumes that original instead of replacing its facts.</summary>
    public Task<NormalizationPeopleResolutionAttempt> SavePreparationAsync(
        Guid attemptUuid,
        NormalizationPeopleResolutionPreparation preparation,
        CancellationToken ct = default
    )
    {
        return InTransaction(
            async (c, t) =>
            {
                var row = await Require(c, t, attemptUuid, ct);
                if (row.PreparationJson != null || row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                // A saved legacy winner is returned before new-format admission. Its old
                // immutable value must not fail replay because a new envelope adds overhead.
                var json = PeopleCheckpointCodec.WritePreparation(preparation);
                var frozen = PeopleCheckpointCodec.ReadPreparation(json);
                await ValidatePreparation(c, t, row.ToAttempt(), frozen, ct);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.people_resolution_attempts SET preparation=@json::jsonb WHERE attempt_uuid=@attemptUuid",
                        new { json, attemptUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                row.PreparationJson = json;
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Completes only the observed preparation; a concurrent winner remains resumable.</summary>
    public Task<NormalizationPeopleResolutionAttempt> CompleteAsync(
        NormalizationPeopleResolutionAttempt expected,
        NormalizationPeopleResolutionDisposition disposition,
        string reason,
        CancellationToken ct = default
    )
    {
        if (
            disposition
            is not (
                NormalizationPeopleResolutionDisposition.Unresolved
                or NormalizationPeopleResolutionDisposition.Stale
            )
        )
        {
            throw new ArgumentException(
                "Only unresolved or stale work completes outside publication."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return InTransaction(
            async (c, t) =>
            {
                var row = await Require(c, t, expected.AttemptUuid, ct);
                if (
                    row.ExtractionAttemptUuid != expected.ExtractionAttemptUuid
                    || row.SourceUuid != expected.SourceUuid
                    || row.ProcessingVersion != expected.ProcessingVersion
                )
                {
                    throw new NormalizationConflictException(
                        "Resolution completion does not match its original request."
                    );
                }
                if (
                    row.Disposition != null
                    || !PeopleCheckpointCodec.EquivalentPreparation(
                        row.PreparationJson == null
                            ? null
                            : PeopleCheckpointCodec.ReadPreparation(row.PreparationJson),
                        expected.Preparation
                    )
                )
                {
                    return row.ToAttempt();
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.people_resolution_attempts SET disposition=@state,reason=@reason,completed_at=clock_timestamp()
                        WHERE attempt_uuid=@AttemptUuid
                        """,
                        new
                        {
                            expected.AttemptUuid,
                            state = disposition.ToString().ToLowerInvariant(),
                            reason,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                row.Disposition = disposition.ToString();
                row.Reason = reason;
                return row.ToAttempt();
            },
            ct
        );
    }

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            ct.ThrowIfCancellationRequested();
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var result = await action((NpgsqlConnection)connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        });

    private static Task<Row?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        bool locked,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(
                $"SELECT {Columns} FROM normalization.people_resolution_attempts WHERE attempt_uuid=@attemptUuid {(locked ? "FOR UPDATE" : "")}",
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static async Task<Row> Require(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, ct)
        ?? throw new ArgumentException("Unknown people resolution attempt.");

    private static Task<string?> ReadBatch(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT batch::text FROM normalization.people_resolution_attempts WHERE attempt_uuid=@attemptUuid",
                new { attemptUuid },
                t,
                cancellationToken: ct
            )
        );

    private static bool Same(string? left, string? right) =>
        left == null || right == null
            ? left == right
            : JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>(left),
                JsonSerializer.Deserialize<JsonElement>(right)
            );

    private sealed class Row
    {
        public Guid AttemptUuid { get; init; }
        public Guid SourceUuid { get; init; }
        public Guid ExtractionAttemptUuid { get; init; }
        public string ProcessingVersion { get; init; } = null!;
        public string? PreparationJson { get; set; }
        public string? Disposition { get; set; }
        public string? Reason { get; set; }

        public NormalizationPeopleResolutionAttempt ToAttempt() =>
            new(
                AttemptUuid,
                SourceUuid,
                ExtractionAttemptUuid,
                ProcessingVersion,
                PreparationJson == null
                    ? null
                    : PeopleCheckpointCodec.ReadPreparation(PreparationJson),
                Disposition == null
                    ? null
                    : new(
                        AttemptUuid,
                        SourceUuid,
                        Enum.Parse<NormalizationPeopleResolutionDisposition>(Disposition, true),
                        Reason!
                    )
            );
    }
}
