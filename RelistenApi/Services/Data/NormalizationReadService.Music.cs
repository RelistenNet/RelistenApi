using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    private const string MusicRevisionColumns =
        "r.revision,r.created_at AS updated_at,r.decision_uuid,r.basis,x.revision_uuid ";
    private const string MusicEntityColumns =
        "x.uuid,x.display_title,x.aliases,r.revision,r.created_at AS updated_at,lower(x.display_title) AS page_sort ";

    /// <summary>Includes title-derived songs by default. Equal titles do not merge artist repertoire identities.</summary>
    public Task<NormalizationPage<NormalizedRepertoireSong>> ListRepertoireAsync(
        string artist,
        string? q = null,
        string? basis = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = NormalizeQuery(q);
        if (basis != null && basis is not ("provider_assertion" or "reviewed" or "title_derived"))
        {
            throw new ArgumentException("Unknown repertoire basis.", nameof(basis));
        }
        var filter = $"{artist}|{query}|{basis}";
        var after = ParseCursor(cursor, "artist_songs", Guid.Empty, filter, "title_uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var artistUuid = await ResolveMusicArtistAsync(c, t, artist, cancellationToken);
                var rows = (
                    await c.QueryAsync<NormalizedRepertoireSong>(
                        new CommandDefinition(
                            """
                            WITH RECURSIVE names AS (
                                SELECT uuid AS owner,uuid,display_title,aliases
                                FROM normalization.repertoire_songs
                                WHERE NOT retracted AND redirect_uuid IS NULL AND artist_uuid=@artistUuid
                                UNION
                                SELECT n.owner,p.uuid,p.display_title,p.aliases
                                FROM names n JOIN normalization.repertoire_songs p ON p.redirect_uuid=n.uuid
                                WHERE NOT p.retracted
                            )
                            """
                                + " SELECT "
                                + MusicEntityColumns
                                + """
                                ,x.artist_uuid,r.basis,link.composition_uuid
                                FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                                LEFT JOIN normalization.repertoire_compositions link ON link.repertoire_song_uuid=x.uuid AND NOT link.retracted
                                    AND EXISTS(SELECT 1 FROM normalization.compositions work WHERE work.uuid=link.composition_uuid AND NOT work.retracted AND work.redirect_uuid IS NULL)
                                WHERE x.artist_uuid=@artistUuid AND NOT x.retracted AND x.redirect_uuid IS NULL
                                    AND (@basis::text IS NULL OR r.basis=@basis)
                                    AND (@query::text IS NULL OR EXISTS(SELECT 1 FROM names n WHERE n.owner=x.uuid
                                        AND (strpos(normalization.music_name_key(n.display_title),normalization.music_name_key(@query))>0 OR EXISTS(SELECT 1 FROM unnest(n.aliases) alias WHERE strpos(normalization.music_name_key(alias),normalization.music_name_key(@query))>0))))
                                    AND (@afterUuid::uuid IS NULL OR (lower(x.display_title) COLLATE "C",x.uuid)>(@sort COLLATE "C",@afterUuid))
                                ORDER BY lower(x.display_title) COLLATE "C",x.uuid LIMIT @take
                                """,
                            new
                            {
                                artistUuid,
                                query,
                                basis,
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
                    x =>
                        EncodeCursor(
                            "artist_songs",
                            Guid.Empty,
                            filter,
                            "title_uuid",
                            x.page_sort,
                            x.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    public Task<NormalizedRepertoireSong?> GetSongAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveMusicAsync(c, t, uuid, true, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                var song = await c.QuerySingleAsync<NormalizedRepertoireSong>(
                    new CommandDefinition(
                        "SELECT "
                            + MusicEntityColumns
                            + """
                            ,x.artist_uuid,r.basis,link.composition_uuid
                            FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                            LEFT JOIN normalization.repertoire_compositions link ON link.repertoire_song_uuid=x.uuid AND NOT link.retracted
                                    AND EXISTS(SELECT 1 FROM normalization.compositions work WHERE work.uuid=link.composition_uuid AND NOT work.retracted AND work.redirect_uuid IS NULL)
                            WHERE x.uuid=@survivor
                            """,
                        new { survivor },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                song.requested_uuid = survivor != uuid ? uuid : null;
                song.redirect_uuid = survivor != uuid ? survivor : null;
                song.imported_song_uuids = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            "SELECT imported_song_uuid FROM normalization.imported_song_assignments WHERE repertoire_song_uuid=@survivor AND NOT retracted ORDER BY imported_song_uuid",
                            new { survivor },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                song.composition = (
                    await MusicLinksAsync(c, t, survivor, null, cancellationToken)
                ).SingleOrDefault();
                song.external_identities = await MusicExternalIdentitiesAsync(
                    c,
                    t,
                    survivor.Value,
                    true,
                    cancellationToken
                );
                return song;
            },
            cancellationToken
        );

    public Task<NormalizationPage<NormalizedComposition>> ListCompositionsAsync(
        string? q = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = NormalizeQuery(q);
        var after = ParseCursor(cursor, "compositions", Guid.Empty, query, "title_uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var rows = (
                    await c.QueryAsync<NormalizedComposition>(
                        new CommandDefinition(
                            """
                            WITH RECURSIVE names AS (
                                SELECT uuid AS owner,uuid,display_title,aliases
                                FROM normalization.compositions WHERE NOT retracted AND redirect_uuid IS NULL
                                UNION
                                SELECT n.owner,p.uuid,p.display_title,p.aliases
                                FROM names n JOIN normalization.compositions p ON p.redirect_uuid=n.uuid
                                WHERE NOT p.retracted
                            )
                            """
                                + " SELECT "
                                + MusicEntityColumns
                                + """
                                FROM normalization.compositions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                                WHERE NOT x.retracted AND x.redirect_uuid IS NULL
                                    AND (@query::text IS NULL OR EXISTS(SELECT 1 FROM names n WHERE n.owner=x.uuid
                                        AND (strpos(normalization.music_name_key(n.display_title),normalization.music_name_key(@query))>0 OR EXISTS(SELECT 1 FROM unnest(n.aliases) alias WHERE strpos(normalization.music_name_key(alias),normalization.music_name_key(@query))>0))))
                                    AND (@afterUuid::uuid IS NULL OR (lower(x.display_title) COLLATE "C",x.uuid)>(@sort COLLATE "C",@afterUuid))
                                ORDER BY lower(x.display_title) COLLATE "C",x.uuid LIMIT @take
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
                    x =>
                        EncodeCursor(
                            "compositions",
                            Guid.Empty,
                            query,
                            "title_uuid",
                            x.page_sort,
                            x.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    public Task<NormalizedComposition?> GetCompositionAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveMusicAsync(c, t, uuid, false, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                var work = await c.QuerySingleAsync<NormalizedComposition>(
                    new CommandDefinition(
                        "SELECT "
                            + MusicEntityColumns
                            + " FROM normalization.compositions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@survivor",
                        new { survivor },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                work.requested_uuid = survivor != uuid ? uuid : null;
                work.redirect_uuid = survivor != uuid ? survivor : null;
                work.repertoire_links = await MusicLinksAsync(
                    c,
                    t,
                    null,
                    survivor,
                    cancellationToken
                );
                var credits = (
                    await c.QueryAsync<NormalizedWorkCredit>(
                        new CommandDefinition(
                            "SELECT x.uuid,x.role,x.original_text,x.person_uuid,x.artist_uuid,"
                                + MusicRevisionColumns
                                + " FROM normalization.work_credits x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.composition_uuid=@survivor AND NOT x.retracted ORDER BY x.uuid",
                            new { survivor },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                await PopulateMusicEvidenceAsync(c, t, credits, cancellationToken);
                work.work_credits = credits;
                work.external_identities = await MusicExternalIdentitiesAsync(
                    c,
                    t,
                    survivor.Value,
                    false,
                    cancellationToken
                );
                return work;
            },
            cancellationToken
        );

    private static Task<Guid?> ResolveMusicAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid uuid,
        bool repertoire,
        CancellationToken ct
    )
    {
        var table = repertoire ? "repertoire_songs" : "compositions";
        return connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                $"""
                WITH RECURSIVE identities AS (
                    SELECT uuid,redirect_uuid FROM normalization.{table} WHERE uuid=@uuid AND NOT retracted
                    UNION SELECT x.uuid,x.redirect_uuid FROM normalization.{table} x JOIN identities i ON x.uuid=i.redirect_uuid WHERE NOT x.retracted)
                SELECT uuid FROM identities WHERE redirect_uuid IS NULL
                """,
                new { uuid },
                transaction,
                cancellationToken: ct
            )
        );
    }

    private static async Task<Guid> ResolveMusicArtistAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string artist,
        CancellationToken ct
    )
    {
        var uuid = Guid.TryParse(artist, out var parsed) ? parsed : (Guid?)null;
        return await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT uuid FROM artists WHERE (@uuid::uuid IS NOT NULL AND uuid=@uuid) OR (@uuid::uuid IS NULL AND slug=@artist)",
                    new { uuid, artist },
                    t,
                    cancellationToken: ct
                )
            ) ?? throw new NormalizationEntityNotFoundException();
    }

    private static async Task<NormalizedCompositionLink[]> MusicLinksAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? song,
        Guid? work,
        CancellationToken ct
    )
    {
        var links = (
            await c.QueryAsync<NormalizedCompositionLink>(
                new CommandDefinition(
                    "SELECT x.uuid,x.repertoire_song_uuid,x.composition_uuid,r.payload->>'MatchReason' AS match_reason,"
                        + MusicRevisionColumns
                        + """
                        FROM normalization.repertoire_compositions x
                        JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                        JOIN normalization.repertoire_songs song ON song.uuid=x.repertoire_song_uuid
                            AND NOT song.retracted AND song.redirect_uuid IS NULL
                        JOIN normalization.compositions work ON work.uuid=x.composition_uuid
                            AND NOT work.retracted AND work.redirect_uuid IS NULL
                        WHERE NOT x.retracted
                            AND (@song::uuid IS NULL OR x.repertoire_song_uuid=@song)
                            AND (@work::uuid IS NULL OR x.composition_uuid=@work)
                        ORDER BY x.uuid
                        """,
                    new { song, work },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        await PopulateMusicEvidenceAsync(c, t, links, ct);
        return links;
    }

    private sealed class MusicEvidenceRow : NormalizationPublicEvidence
    {
        public Guid revision_uuid { get; set; }
    }

    private static async Task PopulateMusicEvidenceAsync<T>(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IEnumerable<T> rows,
        CancellationToken ct
    )
        where T : NormalizedMusicRelationship
    {
        var relationships = rows.ToArray();
        if (relationships.Length == 0)
        {
            return;
        }
        var evidence = (
            await c.QueryAsync<MusicEvidenceRow>(
                new CommandDefinition(
                    "SELECT e.revision_uuid,"
                        + EvidenceColumns
                        + " FROM normalization.music_evidence_references e "
                        + EvidenceJoins
                        + " WHERE e.revision_uuid=ANY(@revisions) ORDER BY e.revision_uuid,e.ordinal",
                    new
                    {
                        revisions = relationships.Select(x => x.revision_uuid).Distinct().ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToLookup(x => x.revision_uuid);
        foreach (var row in relationships)
        {
            row.evidence = evidence[row.revision_uuid]
                .Select(x => new NormalizationPublicEvidence
                {
                    input_revision_uuid = x.input_revision_uuid,
                    json_pointer = x.json_pointer,
                    start_code_point = x.start_code_point,
                    end_code_point = x.end_code_point,
                    quote = x.quote,
                    evidence_status = x.evidence_status,
                })
                .ToArray();
            row.evidence_status =
                row.evidence.Any(x => x.evidence_status == "missing") ? "missing"
                : row.evidence.Any(x => x.evidence_status == "changed") ? "changed"
                : row.evidence.Count > 0 ? "current"
                : "missing";
        }
    }

    private static async Task<NormalizationExternalIdentity[]> MusicExternalIdentitiesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        bool repertoire,
        CancellationToken ct
    )
    {
        var column = repertoire ? "repertoire_song_uuid" : "composition_uuid";
        return (
            await c.QueryAsync<NormalizationExternalIdentity>(
                new CommandDefinition(
                    "SELECT x.provider,x.resource_type,x.provider_key,mrev.revision,mrev.basis,"
                        + EvidenceColumns
                        + $" FROM normalization.music_external_identities x JOIN normalization.music_revisions mrev ON mrev.uuid=x.revision_uuid JOIN normalization.music_evidence_references e ON e.revision_uuid=x.revision_uuid "
                        + EvidenceJoins
                        + $" WHERE x.{column}=@uuid AND NOT x.retracted ORDER BY x.provider,x.resource_type,x.provider_key,e.ordinal",
                    new { uuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }
}
