using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task<RepertoireResolutionAssociation?> ReadRepertoireAssociation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid registration,
        string correspondence,
        CancellationToken ct,
        Guid? throughDecision = null,
        bool skipRetained = false,
        Guid? skipAttempt = null
    )
    {
        // Accepted stages are durable before their account's final acknowledgement. Looking
        // only through current_attempt_uuid would allocate duplicates after a partial retry.
        var row = await c.QuerySingleOrDefaultAsync<RepertoireAssociationRow>(
            new CommandDefinition(
                """
                SELECT decision_uuid AS "DecisionUuid",registration_uuid AS "RegistrationUuid",attempt_uuid AS "AttemptUuid",
                    claim_uuid AS "ClaimUuid",correspondence_key AS "CorrespondenceKey",repertoire_song_uuid AS "RepertoireSongUuid",
                    disposition AS "Disposition",reason AS "Reason",authority AS "Authority",
                    CASE WHEN octet_length(evidence::text)<=1048576 THEN evidence::text END AS "Evidence"
                FROM normalization.repertoire_resolution_associations
                WHERE registration_uuid=@registration AND correspondence_key=@correspondence
                    AND (@throughDecision::uuid IS NULL OR sequence<=(SELECT sequence FROM normalization.repertoire_resolution_associations WHERE decision_uuid=@throughDecision))
                    AND (NOT @skipRetained OR authority<>'retained_correspondence')
                    AND (@skipAttempt::uuid IS NULL OR attempt_uuid<>@skipAttempt)
                ORDER BY sequence DESC LIMIT 1
                """,
                new
                {
                    registration,
                    correspondence,
                    throughDecision,
                    skipRetained,
                    skipAttempt,
                },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        if (row.Evidence == null)
        {
            throw new InvalidOperationException(
                "The repertoire correspondence evidence exceeds its bound."
            );
        }
        return new(
            row.DecisionUuid,
            row.RegistrationUuid,
            row.AttemptUuid,
            row.ClaimUuid,
            row.CorrespondenceKey,
            row.RepertoireSongUuid,
            Enum.Parse<RepertoireResolutionClaimDisposition>(row.Disposition),
            row.Reason,
            JsonSerializer.Deserialize<NormalizedEvidenceReference[]>(row.Evidence)!,
            row.Authority
        );
    }

    private static Task<ImportedSongAssignmentChange[]> ReadRepertoireImportedAssignments(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? imported,
        CancellationToken ct
    ) =>
        imported is { } uuid
            ? ReadRepertoireAssertions(
                c,
                t,
                "imported_song_assignments",
                "x.imported_song_uuid=ANY(@ids)",
                [uuid],
                (ImportedSongAssignmentChange value, RepertoireRevisionRow row) =>
                    value with
                    {
                        ExpectedRevision = row.Revision,
                        Basis = row.Basis,
                        Evidence = row.Citations,
                        Retracted = row.Retracted,
                    },
                ct,
                includeReviewedTombstones: true
            )
            : Task.FromResult(Array.Empty<ImportedSongAssignmentChange>());

    private static Task<RepertoireSongChange[]> ReadRepertoireSongs(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] ids,
        CancellationToken ct
    ) =>
        ReadRepertoireAssertions(
            c,
            t,
            "repertoire_songs",
            "x.uuid=ANY(@ids)",
            ids,
            (RepertoireSongChange value, RepertoireRevisionRow row) =>
                value with
                {
                    ExpectedRevision = row.Revision,
                    Basis = row.Basis,
                    Evidence = row.Citations,
                    Retracted = row.Retracted,
                },
            ct
        );

    private static Task<MusicExternalIdentityChange[]> ReadRepertoireExternalIdentities(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] ids,
        CancellationToken ct
    ) =>
        ReadRepertoireAssertions(
            c,
            t,
            "music_external_identities",
            "x.repertoire_song_uuid=ANY(@ids)",
            ids,
            (MusicExternalIdentityChange value, RepertoireRevisionRow row) =>
                value with
                {
                    ExpectedRevision = row.Revision,
                    Basis = row.Basis,
                    Evidence = row.Citations,
                    Retracted = row.Retracted,
                },
            ct,
            includeReviewedTombstones: true
        );

    private static async Task<T[]> ReadRepertoireAssertions<T>(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        string predicate,
        Guid[] ids,
        Func<T, RepertoireRevisionRow, T> current,
        CancellationToken ct,
        bool includeReviewedTombstones = false
    )
    {
        if (ids.Length == 0)
        {
            return [];
        }
        var currentState = includeReviewedTombstones
            ? "(NOT x.retracted OR r.basis='reviewed')"
            : "NOT x.retracted";
        // Only fixed private table/predicate constants reach this query. Preflight immutable
        // revision bytes in the same RR snapshot before materializing complete assertions.
        var bound = await c.QuerySingleAsync<RepertoirePayloadBound>(
            new CommandDefinition(
                $"""
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(r.payload::text)+octet_length(r.evidence::text)),0)::bigint AS "Bytes",
                    COALESCE(max(octet_length(r.payload::text)+octet_length(r.evidence::text)),0)::bigint AS "Largest"
                FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                WHERE {predicate} AND {currentState}
                """,
                new { ids },
                t,
                cancellationToken: ct
            )
        );
        RequireRepertoirePayloadBound(bound);
        var rows = await c.QueryAsync<RepertoireRevisionRow>(
            new CommandDefinition(
                $"""
                SELECT r.revision AS "Revision",r.basis AS "Basis",x.retracted AS "Retracted",
                    r.payload::text AS "Payload",r.evidence::text AS "Evidence"
                FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                WHERE {predicate} AND {currentState} ORDER BY x.uuid
                """,
                new { ids },
                t,
                cancellationToken: ct
            )
        );
        return rows.Select(row => current(JsonSerializer.Deserialize<T>(row.Payload)!, row))
            .ToArray();
    }

    private static async Task<MusicExternalIdentityChange[]> ReadRepertoireClaimIdentities(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        RepertoireResolutionClaim claim,
        CancellationToken ct
    )
    {
        var keys = claim
            .Identities.Concat(claim.AliasOf is { } alias ? [alias] : [])
            .Select(value => value.Identity)
            .Distinct()
            .ToArray();
        if (keys.Length == 0 || keys.Length > 16)
        {
            return [];
        }
        var ids = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT x.uuid FROM normalization.music_external_identities x
                    JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.artist_uuid=@artist AND (NOT x.retracted OR r.basis='reviewed') AND EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@keys::jsonb) k("Provider" text,"ResourceType" text,"ProviderKey" text)
                        WHERE x.provider=k."Provider" AND x.resource_type=k."ResourceType" AND x.provider_key=k."ProviderKey")
                    ORDER BY x.uuid LIMIT 5001
                    """,
                    new { artist, keys = JsonSerializer.Serialize(keys) },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (ids.Length > 5000)
        {
            throw new InvalidOperationException(
                "The exact repertoire identity assertions exceed their bound."
            );
        }
        return await ReadRepertoireAssertions(
            c,
            t,
            "music_external_identities",
            "x.uuid=ANY(@ids)",
            ids,
            (MusicExternalIdentityChange value, RepertoireRevisionRow row) =>
                value with
                {
                    ExpectedRevision = row.Revision,
                    Basis = row.Basis,
                    Evidence = row.Citations,
                    Retracted = row.Retracted,
                },
            ct,
            includeReviewedTombstones: true
        );
    }

    private static async Task ReadRepertoireRelationshipOwners(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] targets,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var owners = (
            await c.QueryAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT owner.uuid FROM unnest(@targets::uuid[]) target(uuid)
                    LEFT JOIN normalization.repertoire_songs song ON song.uuid=target.uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain='songs'
                        AND owner.subject_kind='artist_repertoire' AND owner.subject_uuid=song.artist_uuid
                    """,
                    new { targets },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        foreach (var owner in owners)
        {
            context.AddOwner(owner);
        }
    }

    private static async Task<MusicPublication[]> ReadRepertoireExclusions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? owner,
        CancellationToken ct
    )
    {
        if (owner == null)
        {
            return [];
        }
        const string predicate =
            "EXISTS(SELECT 1 FROM normalization.music_exclusions e WHERE e.decision_uuid=r.uuid) AND (r.subject_uuid=@owner OR @owner=ANY(r.additional_subject_uuids))";
        var bound = await c.QuerySingleAsync<RepertoirePayloadBound>(
            new CommandDefinition(
                $"""
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(r.payload::text)),0)::bigint AS "Bytes",
                    COALESCE(max(octet_length(r.payload::text)),0)::bigint AS "Largest"
                FROM normalization.review_operations r WHERE {predicate}
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
        RequireRepertoirePayloadBound(bound);
        var rows = await c.QueryAsync<string>(
            new CommandDefinition(
                $"""
                SELECT r.payload::text FROM normalization.review_operations r WHERE {predicate} ORDER BY r.uuid
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
        return rows.Select(value => JsonSerializer.Deserialize<MusicPublication>(value)!).ToArray();
    }

    private static void RequireRepertoirePayloadBound(RepertoirePayloadBound bound)
    {
        if (bound.Count > 5000 || bound.Bytes > 32 * 1024 * 1024 || bound.Largest > 1024 * 1024)
        {
            throw new InvalidOperationException(
                "The complete repertoire assertion snapshot exceeds its bounded projection."
            );
        }
    }

    private sealed record RepertoirePayloadBound(long Count, long Bytes, long Largest);

    private sealed record RepertoireRevisionRow(
        long Revision,
        string Basis,
        bool Retracted,
        string Payload,
        string Evidence
    )
    {
        public NormalizedEvidenceReference[] Citations =>
            JsonSerializer.Deserialize<NormalizedEvidenceReference[]>(Evidence)!;
    }

    private sealed record RepertoireAssociationRow(
        Guid DecisionUuid,
        Guid RegistrationUuid,
        Guid AttemptUuid,
        Guid ClaimUuid,
        string CorrespondenceKey,
        Guid? RepertoireSongUuid,
        string Disposition,
        string Reason,
        string Authority,
        string? Evidence
    );
}
