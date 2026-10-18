using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializationStore
{
    private static async Task<RecordingContentMaterializationAttempt> RequireAttempt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        await Read(c, t, uuid, true, true, ct)
        ?? throw new ArgumentException("Unknown materialization attempt.");

    private static async Task<RecordingContentMaterializationAttempt?> Read(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        bool locked,
        bool full,
        CancellationToken ct
    )
    {
        if (locked)
        {
            await c.ExecuteScalarAsync<Guid?>(
                new CommandDefinition(
                    "SELECT attempt_uuid FROM normalization.recording_content_materialization_attempts WHERE attempt_uuid=@uuid FOR UPDATE",
                    new { uuid },
                    t,
                    cancellationToken: ct
                )
            );
        }

        // Measure after the header lock, using exactly the projection that will be materialized.
        await RequireSize(c, t, new[] { uuid }, full, ct);
        var preparationProjection = full ? "a.preparation" : SummaryPreparation;
        var ticketProjection = full ? "a.ticket::text" : "NULL::text";
        var row = await c.QuerySingleOrDefaultAsync<AttemptRow>(
            new CommandDefinition(
                $"""
                SELECT attempt_uuid AS "Uuid",registration::text AS "Registration",request::text AS "Request",
                    retry_selection::text AS "Retry",selected_repertoire_attempt_uuid AS "Selected",processing_version AS "Version",
                    ({preparationProjection})::text AS "Preparation",{ticketProjection} AS "Ticket",
                    receipt::text AS "Receipt",result::text AS "Result",checkpoint_revision AS "Revision",
                    admission_failure_reason AS "Failure"
                FROM normalization.recording_content_materialization_attempts a WHERE attempt_uuid=@uuid
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }

        return DecodeAttempt(row);
    }

    private static RecordingContentMaterializationAttempt DecodeAttempt(AttemptRow row) =>
        new(
            AttemptUuid: row.Uuid,
            Registration: JsonSerializer.Deserialize<RecordingContentMaterializationRegistration>(
                row.Registration
            )!,
            Request: row.Request == null
                ? null
                : JsonSerializer.Deserialize<RecordingContentMaterializationRequest>(row.Request),
            ProcessingVersion: row.Version,
            RetrySelection: row.Retry == null
                ? null
                : JsonSerializer.Deserialize<MusicMaterializationRetrySelection>(row.Retry),
            SelectedRepertoireAttemptUuid: row.Selected,
            Preparation: row.Preparation == null
                ? null
                : JsonSerializer.Deserialize<RecordingContentMaterializationPreparation>(
                    row.Preparation
                ),
            Ticket: row.Ticket == null
                ? null
                : JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket),
            Receipt: row.Receipt == null
                ? null
                : JsonSerializer.Deserialize<NormalizationPublicationResult>(row.Receipt),
            Result: row.Result == null
                ? null
                : JsonSerializer.Deserialize<MusicMaterializationResult>(row.Result),
            CheckpointRevision: row.Revision,
            AdmissionFailureReason: row.Failure
        );

    private static async Task<RecordingContentMaterializationRegistration?> ReadRegistration(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<RegistrationRow>(
            new CommandDefinition(
                "SELECT registration_uuid AS \"Uuid\",source_uuid AS \"Lineage\",current_attempt_uuid AS \"Current\" FROM normalization.recording_content_materialization_registrations WHERE registration_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null ? null : new(row.Uuid, row.Lineage, row.Current);
    }

    private static async Task RequireSelection(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingContentMaterializationRegistration registration,
        Guid selected,
        CancellationToken ct
    )
    {
        var valid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_attempts a
                    JOIN normalization.repertoire_resolution_registrations r ON r.registration_uuid=a.registration_uuid AND r.current_attempt_uuid=a.attempt_uuid
                    JOIN normalization.recording_processing_attempts p ON p.attempt_uuid=(a.request->'Source'->>'PublishedProcessingAttemptUuid')::uuid
                    WHERE a.attempt_uuid=@selected AND (a.result->>'Disposition')::integer=1
                        AND a.request->'Source'->>'kind'='recording_titles' AND p.source_uuid=@lineage)
                """,
                new { selected, lineage = registration.SourceUuid },
                t,
                cancellationToken: ct
            )
        );
        if (!valid)
        {
            throw new NormalizationConflictException(
                "Select a current completed repertoire account for this exact extraction lineage."
            );
        }
    }

    public Task<RecordingContentMaterializationAttempt?> GetLatestAttemptAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        InTransaction<RecordingContentMaterializationAttempt?>(
            async (c, t) =>
            {
                var uuid = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT a.attempt_uuid FROM normalization.recording_content_materialization_attempts a WHERE registration_uuid=@registrationUuid AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts child WHERE child.previous_attempt_uuid=a.attempt_uuid) ORDER BY sequence DESC LIMIT 1 FOR UPDATE OF a",
                        new { registrationUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                return uuid == null ? null : await Read(c, t, uuid.Value, false, false, ct);
            },
            ct
        );

    public Task<RecordingContentMaterializationRecoveryPage> GetRecoveryAttemptsAsync(
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken ct = default
    )
    {
        ValidatePage(limit, afterUuid);
        return InTransaction(
            async (c, t) =>
            {
                var ids = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            """
                            SELECT a.attempt_uuid FROM normalization.recording_content_materialization_attempts a
                            WHERE (@afterUuid::uuid IS NULL OR a.attempt_uuid>@afterUuid)
                                AND (a.result IS NULL OR ((a.result->>'Disposition')::integer=2
                                    AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts newer_leaf
                                        WHERE newer_leaf.registration_uuid=a.registration_uuid AND newer_leaf.sequence>a.sequence
                                            AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts child
                                                WHERE child.previous_attempt_uuid=newer_leaf.attempt_uuid))
                                    AND (
                                    COALESCE(a.result->'RetryRequirement','null'::jsonb)='null'::jsonb
                                    OR a.result->'RetryRequirement'->>'ProcessorVersion' IS DISTINCT FROM @version
                                    OR a.result->'RetryRequirement'->>'LimitProfile' IS DISTINCT FROM @profile
                                    OR NOT (COALESCE(a.result->'RetryRequirement'->'CapacityOwner','null'::jsonb)='null'::jsonb
                                    OR EXISTS(SELECT 1 FROM normalization.work_subjects capacity_owner
                                        WHERE capacity_owner.uuid=(a.result->'RetryRequirement'->'CapacityOwner'->>'SubjectUuid')::uuid
                                            AND capacity_owner.desired_generation=(a.result->'RetryRequirement'->'CapacityOwner'->>'DesiredGeneration')::bigint
                                            AND capacity_owner.editorial_revision=(a.result->'RetryRequirement'->'CapacityOwner'->>'EditorialRevision')::bigint
                                            AND capacity_owner.published_work_uuid IS NOT DISTINCT FROM (a.result->'RetryRequirement'->'CapacityOwner'->>'PublishedWorkUuid')::uuid
                                            AND (COALESCE(a.result->'RetryRequirement'->'CapacityMembership','null'::jsonb)='null'::jsonb
                                                OR ((SELECT count(*) FROM normalization.work_input_contributions member WHERE member.subject_uuid=capacity_owner.uuid)
                                                        =(a.result->'RetryRequirement'->'CapacityMembership'->>'Contributors')::bigint
                                                    AND (SELECT COALESCE(sum(member.revision::numeric),0)::text FROM normalization.work_input_contributions member
                                                        WHERE member.subject_uuid=capacity_owner.uuid)=a.result->'RetryRequirement'->'CapacityMembership'->>'RevisionSum'))))
                                    OR EXISTS(SELECT 1 FROM normalization.repertoire_resolution_attempts selected
                                        JOIN normalization.repertoire_resolution_registrations account ON account.registration_uuid=selected.registration_uuid
                                        JOIN normalization.repertoire_resolution_attempts newer ON newer.attempt_uuid=account.current_attempt_uuid
                                        WHERE selected.attempt_uuid=a.selected_repertoire_attempt_uuid
                                            AND newer.attempt_uuid<>selected.attempt_uuid AND (newer.result->>'Disposition')::integer=1))))
                                AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts child WHERE child.previous_attempt_uuid=a.attempt_uuid)
                            ORDER BY a.attempt_uuid LIMIT @take FOR UPDATE OF a
                            """,
                            new
                            {
                                afterUuid,
                                take = limit + 1,
                                version = NormalizationRecordingContentMaterializer.Version,
                                profile = NormalizationRecordingContentMaterializer.LimitProfile,
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = ids.Take(limit).ToArray();
                // A fresh statement measures payloads only after checkpoint writers released headers.
                await RequireSize(c, t, selected, false, ct);
                var attempts = new RecordingContentMaterializationAttempt[selected.Length];
                for (var index = 0; index < selected.Length; index++)
                {
                    attempts[index] = (await Read(c, t, selected[index], false, false, ct))!;
                }
                return new RecordingContentMaterializationRecoveryPage(
                    attempts,
                    ids.Length > limit ? selected[^1] : null
                );
            },
            ct
        );
    }

    public Task<RecordingContentMaterializationDecisionPage> GetDecisionsAsync(
        Guid attemptUuid,
        int limit = 100,
        Guid? afterDecisionUuid = null,
        CancellationToken ct = default
    )
    {
        ValidatePage(limit, afterDecisionUuid);
        return InTransaction(
            async (c, t) =>
            {
                var ids = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            "SELECT uuid FROM normalization.recording_content_materialization_decisions WHERE attempt_uuid=@attemptUuid AND (@afterDecisionUuid::uuid IS NULL OR uuid>@afterDecisionUuid) ORDER BY uuid LIMIT @take",
                            new
                            {
                                attemptUuid,
                                afterDecisionUuid,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = ids.Take(limit).ToArray();
                var bytes = await c.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        "SELECT COALESCE(sum(octet_length(decision::text)),0)::bigint FROM normalization.recording_content_materialization_decisions WHERE attempt_uuid=@attemptUuid AND uuid=ANY(@selected)",
                        new { attemptUuid, selected },
                        t,
                        cancellationToken: ct
                    )
                );
                if (bytes > MaximumDecisionPageBytes)
                {
                    throw new InvalidOperationException(
                        "Decision page exceeds 32 MiB; request a smaller page."
                    );
                }
                var rows = await c.QueryAsync<string>(
                    new CommandDefinition(
                        "SELECT decision::text FROM normalization.recording_content_materialization_decisions WHERE attempt_uuid=@attemptUuid AND uuid=ANY(@selected) ORDER BY uuid",
                        new { attemptUuid, selected },
                        t,
                        cancellationToken: ct
                    )
                );
                return new RecordingContentMaterializationDecisionPage(
                    rows.Select(value =>
                            JsonSerializer.Deserialize<RecordingContentMaterializationDecision>(
                                value
                            )!
                        )
                        .ToArray(),
                    ids.Length > limit ? selected[^1] : null
                );
            },
            ct
        );
    }

    public Task<RecordingContentMaterializationRegistrationPage> GetPendingRegistrationsAsync(
        int limit = 100,
        Guid? afterRegUuid = null,
        CancellationToken ct = default
    )
    {
        ValidatePage(limit, afterRegUuid);
        return InTransaction(
            async (c, t) =>
            {
                var ids = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            PendingSql,
                            new
                            {
                                afterRegUuid,
                                take = limit + 1,
                                version = NormalizationRecordingContentMaterializer.Version,
                                profile = NormalizationRecordingContentMaterializer.LimitProfile,
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = ids.Take(limit).ToArray();
                var registrations = new RecordingContentMaterializationRegistration[
                    selected.Length
                ];
                for (var index = 0; index < selected.Length; index++)
                {
                    registrations[index] = (await ReadRegistration(c, t, selected[index], ct))!;
                }
                return new RecordingContentMaterializationRegistrationPage(
                    registrations,
                    ids.Length > limit ? selected[^1] : null
                );
            },
            ct
        );
    }

    private static void ValidatePage(int limit, Guid? cursor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        if (cursor == Guid.Empty)
        {
            throw new ArgumentException("A cursor is a nonempty UUID.");
        }
    }

    private static async Task RequireSize(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid[] ids,
        bool full,
        CancellationToken ct
    )
    {
        var preparationProjection = full ? "a.preparation" : SummaryPreparation;
        var ticketProjection = full ? "COALESCE(octet_length(a.ticket::text),0)" : "0";
        const string historyProjection = "0";
        var bytes = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                $"""
                SELECT COALESCE(sum(octet_length(registration::text)+COALESCE(octet_length(request::text),0)
                    +COALESCE(octet_length(retry_selection::text),0)+COALESCE(octet_length(({preparationProjection})::text),0)
                    +{ticketProjection}+COALESCE(octet_length(receipt::text),0)+COALESCE(octet_length(result::text),0)
                    +{historyProjection}),0)::bigint
                FROM normalization.recording_content_materialization_attempts a WHERE attempt_uuid=ANY(@ids)
                """,
                new { ids },
                t,
                cancellationToken: ct
            )
        );
        if (
            bytes
            > (full ? MaximumPreparationBytes + MaximumDecisionPageBytes : MaximumSummaryBytes)
        )
        {
            throw new InvalidOperationException(
                "Materialization representation exceeds its bounded read profile; reduce discovery page size."
            );
        }
    }

    private const string SummaryPreparation = """
        (CASE WHEN a.preparation IS NULL THEN NULL ELSE jsonb_build_object('Context',null,'Image',jsonb_build_object('SourceUuid',a.preparation->'Image'->'SourceUuid','Contents','[]'::jsonb,'Correspondence','[]'::jsonb,'Withdrawals','[]'::jsonb),
            'Decisions','[]'::jsonb,'ContextOmitted',true,'TotalDecisions',jsonb_array_length(a.preparation->'Decisions')) END)
        """;
    private const string PendingSql = """
        SELECT r.registration_uuid FROM normalization.recording_content_materialization_registrations r
        LEFT JOIN normalization.work_subjects s ON s.domain='songs' AND s.subject_kind='recording' AND s.subject_uuid=r.source_uuid
        WHERE (@afterRegUuid::uuid IS NULL OR r.registration_uuid>@afterRegUuid)
            -- Permanent failures wait for changed source authority, processor or capacity profile.
            AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts failed
                WHERE failed.attempt_uuid=(SELECT leaf.attempt_uuid FROM normalization.recording_content_materialization_attempts leaf
                    WHERE leaf.registration_uuid=r.registration_uuid
                        AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts child WHERE child.previous_attempt_uuid=leaf.attempt_uuid)
                    ORDER BY leaf.sequence DESC LIMIT 1)
                    AND (failed.result->>'Disposition')::integer=2
                    AND failed.result->'RetryRequirement'->>'ProcessorVersion'=@version
                    AND failed.result->'RetryRequirement'->>'LimitProfile'=@profile
                    AND (COALESCE(failed.result->'RetryRequirement'->'CapacityOwner','null'::jsonb)='null'::jsonb
                        OR EXISTS(SELECT 1 FROM normalization.work_subjects capacity_owner
                            WHERE capacity_owner.uuid=(failed.result->'RetryRequirement'->'CapacityOwner'->>'SubjectUuid')::uuid
                                AND capacity_owner.desired_generation=(failed.result->'RetryRequirement'->'CapacityOwner'->>'DesiredGeneration')::bigint
                                AND capacity_owner.editorial_revision=(failed.result->'RetryRequirement'->'CapacityOwner'->>'EditorialRevision')::bigint
                                AND capacity_owner.published_work_uuid IS NOT DISTINCT FROM (failed.result->'RetryRequirement'->'CapacityOwner'->>'PublishedWorkUuid')::uuid
                                AND (COALESCE(failed.result->'RetryRequirement'->'CapacityMembership','null'::jsonb)='null'::jsonb
                                    OR ((SELECT count(*) FROM normalization.work_input_contributions member WHERE member.subject_uuid=capacity_owner.uuid)
                                            =(failed.result->'RetryRequirement'->'CapacityMembership'->>'Contributors')::bigint
                                        AND (SELECT COALESCE(sum(member.revision::numeric),0)::text FROM normalization.work_input_contributions member
                                            WHERE member.subject_uuid=capacity_owner.uuid)=failed.result->'RetryRequirement'->'CapacityMembership'->>'RevisionSum'))))
                    AND NOT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_attempts selected
                        JOIN normalization.repertoire_resolution_registrations account ON account.registration_uuid=selected.registration_uuid
                        JOIN normalization.repertoire_resolution_attempts newer ON newer.attempt_uuid=account.current_attempt_uuid
                        WHERE selected.attempt_uuid=failed.selected_repertoire_attempt_uuid
                            AND newer.attempt_uuid<>selected.attempt_uuid AND (newer.result->>'Disposition')::integer=1))
            AND (s.uuid IS NULL
                OR EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts failed
                    WHERE failed.attempt_uuid=(SELECT leaf.attempt_uuid FROM normalization.recording_content_materialization_attempts leaf
                        WHERE leaf.registration_uuid=r.registration_uuid
                            AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts child WHERE child.previous_attempt_uuid=leaf.attempt_uuid)
                        ORDER BY leaf.sequence DESC LIMIT 1)
                        AND (failed.result->>'Disposition')::integer=2
                        AND COALESCE(failed.result->'RetryRequirement','null'::jsonb)<>'null'::jsonb)
                OR (
                            s.desired_generation<>s.published_generation
                            OR EXISTS(SELECT 1 FROM normalization.work_inputs i JOIN normalization.input_resources r ON r.uuid=i.resource_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_dependencies d JOIN normalization.work_subjects ds ON ds.uuid=d.dependency_uuid
                                WHERE d.subject_uuid=s.uuid AND (d.published_generation IS DISTINCT FROM ds.desired_generation
                                    OR d.published_editorial_revision IS DISTINCT FROM ds.editorial_revision
                                    OR d.published_work_uuid IS DISTINCT FROM ds.published_work_uuid))
                            OR EXISTS(SELECT 1 FROM normalization.work_owner_observations i JOIN normalization.work_subjects observed ON observed.uuid=i.observed_subject_uuid
                                WHERE i.subject_uuid=s.uuid AND (i.published_desired_generation IS DISTINCT FROM observed.desired_generation
                                    OR i.published_editorial_revision IS DISTINCT FROM observed.editorial_revision))
                            OR EXISTS(SELECT 1 FROM normalization.work_catalog_inputs i JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=i.artist_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_source_identity_inputs i JOIN normalization.catalog_source_identity_revisions r ON r.upstream_identifier=i.upstream_identifier
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_source_uuid_inputs i JOIN normalization.catalog_source_uuid_revisions r ON r.source_uuid=i.source_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_venue_inputs i JOIN normalization.catalog_venue_revisions r ON r.venue_uuid=i.venue_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_candidate_inputs i JOIN normalization.candidate_universe_revisions r
                                ON r.domain=i.domain AND r.scope_uuid=i.scope_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_mapping_registry_inputs i CROSS JOIN normalization.catalog_mapping_registry_revision r
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
            ))
        ORDER BY r.registration_uuid LIMIT @take
        """;

    private sealed record AttemptRow(
        Guid Uuid,
        string Registration,
        string? Request,
        string? Retry,
        Guid? Selected,
        string Version,
        string? Preparation,
        string? Ticket,
        string? Receipt,
        string? Result,
        long Revision,
        string? Failure
    );

    private sealed record RegistrationRow(Guid Uuid, Guid Lineage, Guid? Current);
}
