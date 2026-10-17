using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    /// <summary>Lists accepted identities by normalized name and UUID. Equal names remain separate people.</summary>
    public Task<NormalizationPage<NormalizedPerson>> ListPeopleAsync(
        string? q = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = NormalizeQuery(q);
        var after = ParseCursor(cursor, "people", Guid.Empty, query, "name_uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var rows = (
                    await c.QueryAsync<NormalizedPerson>(
                        new CommandDefinition(
                            """
                            SELECT p.*,ARRAY(SELECT DISTINCT wording FROM normalization.resolved_person_aliases WHERE resolved_person_uuid=p.uuid ORDER BY wording) AS aliases
                            FROM normalization.people p WHERE NOT EXISTS(SELECT 1 FROM normalization.person_redirects r WHERE r.retired_uuid=p.uuid)
                                AND (@query IS NULL OR strpos(p.name_key,@query)>0 OR EXISTS(
                                    SELECT 1 FROM normalization.person_identity_resolution resolution JOIN normalization.people original ON original.uuid=resolution.original_uuid
                                    WHERE resolution.resolved_uuid=p.uuid AND strpos(original.name_key,@query)>0) OR EXISTS(
                                    SELECT 1 FROM normalization.resolved_person_aliases a WHERE a.resolved_person_uuid=p.uuid AND strpos(a.name_key,@query)>0))
                                AND (@afterUuid IS NULL OR (p.name_key COLLATE "C",p.uuid)>(@sort COLLATE "C",@afterUuid))
                            ORDER BY p.name_key COLLATE "C",p.uuid LIMIT @take
                            """,
                            new
                            {
                                query,
                                afterUuid = after?.Uuid,
                                sort = after?.Sort,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                return Page(
                    rows,
                    limit,
                    p =>
                        EncodeCursor(
                            "people",
                            Guid.Empty,
                            query,
                            "name_uuid",
                            NormalizedPeopleService.NameKey(p.display_name),
                            p.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    /// <summary>A retired identity resolves to its current survivor while retaining the requested UUID.</summary>
    public Task<NormalizedPerson?> GetPersonAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveAsync(c, t, uuid, true, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                var person = await c.QuerySingleAsync<NormalizedPerson>(
                    new CommandDefinition(
                        """
                        SELECT p.*,ARRAY(SELECT DISTINCT wording FROM normalization.resolved_person_aliases WHERE resolved_person_uuid=p.uuid ORDER BY wording) AS aliases
                        FROM normalization.people p WHERE p.uuid=@survivor
                        """,
                        new { survivor },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                person.requested_uuid = survivor != uuid ? uuid : null;
                person.redirect_uuid = survivor != uuid ? survivor : null;
                person.external_identities = (
                    await c.QueryAsync<NormalizationExternalIdentity>(
                        new CommandDefinition(
                            "SELECT e.provider,e.resource_type,e.provider_key,e.revision,e.basis,"
                                + EvidenceColumns
                                + " FROM normalization.resolved_person_external_identities e "
                                + EvidenceJoins
                                + " WHERE e.resolved_person_uuid=@survivor ORDER BY e.provider,e.resource_type,e.provider_key",
                            new { survivor },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                return person;
            },
            cancellationToken
        );

    /// <summary>Returns each currently playable recording once, unioning all matching accepted roles and credit IDs.</summary>
    public Task<NormalizationPage<NormalizedRecordingReference>?> ListPersonRecordingsAsync(
        Guid personUuid,
        string? role = null,
        Guid? artistUuid = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRole(role);
        var filter = $"{role}|{artistUuid:D}";
        var after = ParseCursor(
            cursor,
            "person_recordings",
            personUuid,
            filter,
            "date_desc_uuid",
            limit
        );
        return ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveAsync(c, t, personUuid, true, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                await RequireArtistAsync(c, t, artistUuid, cancellationToken);
                var rows = (
                    await c.QueryAsync<NormalizedRecordingReference>(
                        new CommandDefinition(
                            "WITH playable AS ("
                                + PlayableSources
                                + """
                                ),credits AS (
                                    SELECT credit.source_uuid,array_agg(DISTINCT credit.uuid ORDER BY credit.uuid) AS credit_uuids,
                                        array_agg(DISTINCT credit.role ORDER BY credit.role) AS roles
                                    FROM normalization.recording_credits credit WHERE credit.person_uuid=@survivor AND credit.state='accepted'
                                        AND (@role IS NULL OR credit.role=@role)
                                        AND (credit.scope_track_uuid IS NULL OR EXISTS(
                                            SELECT 1 FROM source_tracks track JOIN sources source ON source.id=track.source_id
                                            JOIN source_sets sets ON sets.id=track.source_set_id AND sets.source_id=source.id
                                            WHERE track.uuid=credit.scope_track_uuid AND source.uuid=credit.source_uuid
                                                AND track.artist_id=source.artist_id AND NOT track.is_orphaned))
                                    GROUP BY credit.source_uuid
                                )
                                SELECT p.source_uuid,p.artist_uuid,p.artist_name,p.artist_slug,p.show_uuid,p.show_date AS date,p.display_date,
                                    p.track_count,p.duration,credits.credit_uuids,credits.roles
                                FROM playable p JOIN credits USING(source_uuid)
                                WHERE (@artistUuid IS NULL OR p.artist_uuid=@artistUuid)
                                    AND (@afterUuid IS NULL OR p.show_date COLLATE "C"<@sort COLLATE "C" OR (p.show_date=@sort AND p.source_uuid>@afterUuid))
                                ORDER BY p.show_date COLLATE "C" DESC,p.source_uuid LIMIT @take
                                """,
                            new
                            {
                                survivor,
                                role,
                                artistUuid,
                                afterUuid = after?.Uuid,
                                sort = after?.Sort,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                return Page(
                    rows,
                    limit,
                    r =>
                        EncodeCursor(
                            "person_recordings",
                            personUuid,
                            filter,
                            "date_desc_uuid",
                            r.date,
                            r.source_uuid
                        )
                );
            },
            cancellationToken
        );
    }

    /// <summary>Reads accepted resolved and unresolved credits even when their catalog reference is no longer available.</summary>
    public Task<NormalizationPage<NormalizedRecordingCredit>?> ListRecordingCreditsAsync(
        Guid sourceUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var after = ParseCursor(cursor, "recording_credits", sourceUuid, null, "uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var exists = await c.QuerySingleAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM sources WHERE uuid=@sourceUuid)
                            OR EXISTS(SELECT 1 FROM normalization.recording_credits WHERE source_uuid=@sourceUuid)
                        """,
                        new { sourceUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                if (!exists)
                {
                    return null;
                }
                var rows = (
                    await c.QueryAsync<NormalizedRecordingCredit>(
                        new CommandDefinition(
                            """
                            SELECT credit.*,EXISTS(SELECT 1 FROM sources source JOIN artists artist ON artist.id=source.artist_id
                                WHERE source.uuid=credit.source_uuid AND (credit.scope_track_uuid IS NULL OR EXISTS(
                                    SELECT 1 FROM source_tracks track JOIN source_sets sets ON sets.id=track.source_set_id AND sets.source_id=source.id
                                    WHERE track.uuid=credit.scope_track_uuid AND track.source_id=source.id AND track.artist_id=source.artist_id AND NOT track.is_orphaned))) AS reference_available
                            FROM normalization.recording_credits credit WHERE credit.source_uuid=@sourceUuid AND credit.state='accepted'
                                AND (@afterUuid IS NULL OR credit.uuid>@afterUuid)
                            ORDER BY credit.uuid LIMIT @take
                            """,
                            new
                            {
                                sourceUuid,
                                afterUuid = after?.Uuid,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                foreach (var credit in rows.Take(limit))
                {
                    credit.evidence = (
                        await c.QueryAsync<NormalizationPublicEvidence>(
                            new CommandDefinition(
                                "SELECT "
                                    + EvidenceColumns
                                    + " FROM normalization.recording_credit_evidence e "
                                    + EvidenceJoins
                                    + " WHERE e.credit_uuid=@uuid AND e.credit_revision=@revision ORDER BY e.input_revision_uuid,e.json_pointer,e.start_code_point",
                                new { credit.uuid, credit.revision },
                                t,
                                cancellationToken: cancellationToken
                            )
                        )
                    ).ToArray();
                    credit.evidence_status =
                        credit.evidence.Count == 0
                        || credit.evidence.Any(e => e.evidence_status == "missing")
                            ? "missing"
                        : credit.evidence.Any(e => e.evidence_status == "changed") ? "changed"
                        : "current";
                }
                return Page(
                    rows,
                    limit,
                    r => EncodeCursor("recording_credits", sourceUuid, null, "uuid", "", r.uuid)
                );
            },
            cancellationToken
        );
    }

    private static void ValidateRole(string? role)
    {
        if (
            role != null
            && role
                is not (
                    "taper"
                    or "transferrer"
                    or "matrix_builder"
                    or "mastering_engineer"
                    or "recording_engineer"
                    or "uploader"
                    or "collector"
                    or "musician"
                    or "other"
                )
        )
        {
            throw new ArgumentException("Unknown recording role.", nameof(role));
        }
    }
}
