using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    public Task<RepertoireResolutionRecoveryPage> GetRecoveryAttemptsAsync(
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
                            SELECT a.attempt_uuid FROM normalization.repertoire_resolution_attempts a
                            WHERE (@afterUuid::uuid IS NULL OR a.attempt_uuid>@afterUuid)
                                AND (a.result IS NULL OR a.result->>'Disposition'=@stale)
                                AND NOT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_attempts child WHERE child.previous_attempt_uuid=a.attempt_uuid)
                            ORDER BY a.attempt_uuid LIMIT @take FOR UPDATE OF a
                            """,
                            new
                            {
                                afterUuid,
                                take = limit + 1,
                                stale = ((int)RepertoireResolutionDisposition.Stale).ToString(
                                    System.Globalization.CultureInfo.InvariantCulture
                                ),
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = ids.Take(limit).ToArray();
                // Header locks are acquired before this fresh statement measures the final stage
                // images. Measuring before a blocked writer commits would defeat the byte bound.
                var bytes = await c.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        PublicSummarySizeSql,
                        new { selected },
                        t,
                        cancellationToken: ct
                    )
                );
                if (bytes > MaximumSummaryBytes)
                {
                    throw new InvalidOperationException(
                        "Repertoire recovery summaries exceed 256 MiB; request a smaller page."
                    );
                }
                var attempts = new RepertoireResolutionAttempt[selected.Length];
                for (var index = 0; index < selected.Length; index++)
                {
                    attempts[index] = (
                        await Read(c, t, selected[index], false, ct, includeSourceClaims: false)
                    )!;
                }
                return new RepertoireResolutionRecoveryPage(
                    attempts,
                    ids.Length > limit ? selected[^1] : null
                );
            },
            ct
        );
    }

    /// <summary>Latest durable leaf, including attempts that failed before a complete account existed.</summary>
    public Task<RepertoireResolutionAttempt?> GetLatestAttemptAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) =>
        InTransaction<RepertoireResolutionAttempt?>(
            async (c, t) =>
            {
                var uuid = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        """
                        SELECT a.attempt_uuid FROM normalization.repertoire_resolution_attempts a
                        JOIN normalization.repertoire_resolution_registrations r ON r.registration_uuid=a.registration_uuid
                        WHERE r.registration_uuid=@registrationUuid AND NOT EXISTS(
                            SELECT 1 FROM normalization.repertoire_resolution_attempts child WHERE child.previous_attempt_uuid=a.attempt_uuid)
                        ORDER BY a.sequence DESC LIMIT 1 FOR UPDATE OF a
                        """,
                        new { registrationUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (uuid == null)
                {
                    return null;
                }
                await RequireSummarySize(c, t, uuid.Value, ct, includeSourceClaims: false);
                return await Read(c, t, uuid.Value, false, ct, includeSourceClaims: false);
            },
            ct
        );

    public Task<RepertoireResolutionDecisionPage> GetDecisionsAsync(
        Guid attemptUuid,
        int limit = 100,
        Guid? afterClaimUuid = null,
        CancellationToken ct = default
    )
    {
        ValidatePage(limit, afterClaimUuid);
        return InTransaction(
            async (c, t) =>
            {
                var ids = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            "SELECT claim_uuid FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@attemptUuid AND (@afterClaimUuid::uuid IS NULL OR claim_uuid>@afterClaimUuid) ORDER BY claim_uuid LIMIT @take",
                            new
                            {
                                attemptUuid,
                                afterClaimUuid,
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
                        "SELECT COALESCE(sum(octet_length(evidence::text)+octet_length(correspondence_key)+octet_length(reason)+octet_length(authority)),0)::bigint FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@attemptUuid AND claim_uuid=ANY(@selected)",
                        new { attemptUuid, selected },
                        t,
                        cancellationToken: ct
                    )
                );
                if (bytes > MaximumCheckpointBytes)
                {
                    throw new InvalidOperationException(
                        "Repertoire decision page exceeds 32 MiB; request a smaller page."
                    );
                }
                var rows = await c.QueryAsync<AssociationRow>(
                    new CommandDefinition(
                        "SELECT decision_uuid AS \"DecisionUuid\",registration_uuid AS \"RegistrationUuid\",attempt_uuid AS \"AttemptUuid\",claim_uuid AS \"ClaimUuid\",correspondence_key AS \"Key\",repertoire_song_uuid AS \"SongUuid\",disposition AS \"Disposition\",reason AS \"Reason\",evidence::text AS \"Evidence\" FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@attemptUuid AND claim_uuid=ANY(@selected) ORDER BY claim_uuid",
                        new { attemptUuid, selected },
                        t,
                        cancellationToken: ct
                    )
                );
                var decisions = rows.Select(value => new RepertoireResolutionAssociation(
                        value.DecisionUuid,
                        value.RegistrationUuid,
                        value.AttemptUuid,
                        value.ClaimUuid,
                        value.Key,
                        value.SongUuid,
                        Enum.Parse<RepertoireResolutionClaimDisposition>(value.Disposition),
                        value.Reason,
                        JsonSerializer.Deserialize<NormalizedEvidenceReference[]>(value.Evidence)!
                    ))
                    .ToArray();
                return new RepertoireResolutionDecisionPage(
                    decisions,
                    ids.Length > limit ? selected[^1] : null
                );
            },
            ct
        );
    }

    public Task<RepertoireResolutionRegistrationPage> GetPendingRegistrationsAsync(
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
                            PendingRegistrationsSql,
                            new { afterRegUuid, take = limit + 1 },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = ids.Take(limit).ToArray();
                var bytes = await c.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        "SELECT COALESCE(sum(octet_length(provider)+octet_length(resource_type)+octet_length(provider_key)),0)::bigint FROM normalization.repertoire_resolution_registrations WHERE registration_uuid=ANY(@selected)",
                        new { selected },
                        t,
                        cancellationToken: ct
                    )
                );
                if (bytes > MaximumCheckpointBytes)
                {
                    throw new InvalidOperationException(
                        "Pending repertoire registrations exceed 32 MiB; request a smaller page."
                    );
                }
                var registrations = new RepertoireResolutionRegistration[selected.Length];
                for (var index = 0; index < selected.Length; index++)
                {
                    registrations[index] = (await ReadRegistration(c, t, selected[index], ct))!;
                }
                return new RepertoireResolutionRegistrationPage(
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
            throw new ArgumentException("A page cursor is a nonempty UUID.");
        }
    }

    private sealed record AssociationRow(
        Guid DecisionUuid,
        Guid RegistrationUuid,
        Guid AttemptUuid,
        Guid ClaimUuid,
        string Key,
        Guid? SongUuid,
        string Disposition,
        string Reason,
        string Evidence
    );

    // Same pending families as Work's public scan, restricted to enrolled source owners.
    private const string PendingRegistrationsSql = """
        SELECT r.registration_uuid FROM normalization.repertoire_resolution_registrations r
        LEFT JOIN normalization.work_subjects s ON s.domain='songs' AND s.subject_kind='repertoire_observation'
            AND s.subject_uuid=r.registration_uuid
        WHERE (@afterRegUuid::uuid IS NULL OR r.registration_uuid>@afterRegUuid)
            AND (s.uuid IS NULL OR (
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
}
