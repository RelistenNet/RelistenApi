using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    /// <summary>Lists provider accounts, including unassigned dates and performances without audio.</summary>
    public Task<NormalizationPage<NormalizedSetlistEdition>> ListArtistSetlistsAsync(
        string artist,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var after = ParseCursor(
            cursor,
            "artist_setlists",
            Guid.Empty,
            artist,
            "date_desc_uuid",
            limit
        );
        return ReadAsync(
            async (c, t) =>
            {
                var artistUuid = await ResolveMusicArtistAsync(c, t, artist, cancellationToken);
                var rows = (
                    await c.QueryAsync<NormalizedSetlistEdition>(
                        new CommandDefinition(
                            "SELECT x.*,"
                                + MusicRevisionColumns
                                + """
                                FROM normalization.setlist_editions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                                WHERE x.artist_uuid=@artistUuid AND NOT x.retracted AND (@afterUuid::uuid IS NULL
                                    OR x.literal_date COLLATE "C"<@sort COLLATE "C" OR (x.literal_date=@sort AND x.uuid>@afterUuid))
                                ORDER BY x.literal_date COLLATE "C" DESC,x.uuid LIMIT @take
                                """,
                            new
                            {
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
                await PopulateMusicEvidenceAsync(c, t, rows, cancellationToken);
                return Page(
                    rows,
                    limit,
                    x =>
                        EncodeCursor(
                            "artist_setlists",
                            Guid.Empty,
                            artist,
                            "date_desc_uuid",
                            x.literal_date,
                            x.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    public Task<NormalizationPage<NormalizedSetlistEdition>?> ListShowSetlistsAsync(
        Guid showUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var after = ParseCursor(cursor, "show_setlists", showUuid, null, "uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                // Retained accounts survive catalog deletion; an existing catalog show with no accounts is an empty page.
                var exists = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM shows WHERE uuid=@showUuid) OR EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE show_uuid=@showUuid AND NOT retracted)",
                        new { showUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                if (!exists)
                {
                    return null;
                }
                var rows = (
                    await c.QueryAsync<NormalizedSetlistEdition>(
                        new CommandDefinition(
                            "SELECT x.*,"
                                + MusicRevisionColumns
                                + " FROM normalization.setlist_editions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.show_uuid=@showUuid AND NOT x.retracted AND (@afterUuid::uuid IS NULL OR x.uuid>@afterUuid) ORDER BY x.uuid LIMIT @take",
                            new
                            {
                                showUuid,
                                afterUuid = after?.Uuid,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                await PopulateMusicEvidenceAsync(c, t, rows, cancellationToken);
                return Page(
                    rows,
                    limit,
                    x => EncodeCursor("show_setlists", showUuid, null, "uuid", "", x.uuid)
                );
            },
            cancellationToken
        );
    }

    /// <summary>Ordered accounts traverse a passage before its components. Unordered accounts expose UUID order only.</summary>
    public Task<NormalizationPage<NormalizedSongOccurrence>?> ListOccurrencesAsync(
        Guid editionUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var after = ParseCursor(
            cursor,
            "setlist_occurrences",
            editionUuid,
            null,
            "passage_path_uuid",
            limit
        );
        return ReadAsync(
            async (c, t) =>
            {
                var edition = await c.QuerySingleOrDefaultAsync<NormalizedSetlistEdition>(
                    new CommandDefinition(
                        "SELECT x.*,"
                            + MusicRevisionColumns
                            + " FROM normalization.setlist_editions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@editionUuid AND NOT x.retracted",
                        new { editionUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                if (edition == null)
                {
                    return null;
                }
                await PopulateMusicEvidenceAsync(c, t, new[] { edition }, cancellationToken);
                var rows = (
                    await c.QueryAsync<NormalizedSongOccurrence>(
                        new CommandDefinition(
                            """
                            WITH RECURSIVE passages AS (
                                SELECT o.uuid, CASE WHEN @ordered THEN lpad(COALESCE(o.position,2147483647)::text,10,'0')||'/'||o.uuid::text ELSE o.uuid::text END AS page_sort,
                                    ARRAY[o.uuid] AS visited
                                FROM normalization.song_occurrences o WHERE o.edition_uuid=@editionUuid AND NOT o.retracted AND o.parent_occurrence_uuid IS NULL
                                UNION ALL
                                SELECT o.uuid,p.page_sort||'/'||lpad(o.component_position::text,10,'0')||'/'||o.uuid::text,p.visited||o.uuid
                                FROM normalization.song_occurrences o JOIN passages p ON p.uuid=o.parent_occurrence_uuid
                                WHERE o.edition_uuid=@editionUuid AND NOT o.retracted AND NOT o.uuid=ANY(p.visited)
                            )
                            SELECT x.*,r.revision,r.created_at AS updated_at,r.decision_uuid,r.basis,x.revision_uuid,
                                CASE WHEN @ordered THEN p.page_sort ELSE x.uuid::text END AS page_sort
                            FROM passages p JOIN normalization.song_occurrences x ON x.uuid=p.uuid JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                            WHERE @afterUuid::uuid IS NULL OR (CASE WHEN @ordered THEN p.page_sort ELSE x.uuid::text END COLLATE "C",x.uuid)>(@sort COLLATE "C",@afterUuid)
                            ORDER BY CASE WHEN @ordered THEN p.page_sort ELSE x.uuid::text END COLLATE "C",x.uuid LIMIT @take
                            """,
                            new
                            {
                                editionUuid,
                                ordered = edition.ordered,
                                afterUuid = after?.Uuid,
                                sort = after?.Sort,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                var availableSongs = await AvailablePassageSongsAsync(
                    c,
                    t,
                    rows.Select(x => x.repertoire_song_uuid),
                    cancellationToken
                );
                foreach (var row in rows)
                {
                    row.edition = edition;
                    if (row.repertoire_song_uuid is Guid song && !availableSongs.Contains(song))
                    {
                        row.repertoire_song_uuid = null;
                    }
                }
                await PopulateMusicEvidenceAsync(c, t, rows, cancellationToken);
                return Page(
                    rows,
                    limit,
                    x =>
                        EncodeCursor(
                            "setlist_occurrences",
                            editionUuid,
                            null,
                            "passage_path_uuid",
                            x.page_sort,
                            x.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    /// <summary>Historical accepted segments remain readable when their original file no longer has a valid playback join.</summary>
    public Task<NormalizationPage<NormalizedTrackContent>?> ListTrackContentsAsync(
        Guid trackUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var after = ParseCursor(cursor, "track_contents", trackUuid, null, "position_uuid", limit);
        int? position = null;
        if (after != null)
        {
            if (
                !int.TryParse(
                    after.Sort,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
                || parsed < 1
            )
            {
                throw new ArgumentException("Invalid file content cursor.", nameof(cursor));
            }
            position = parsed;
        }
        return ReadAsync(
            async (c, t) =>
            {
                var exists = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM source_tracks WHERE uuid=@trackUuid) OR EXISTS(SELECT 1 FROM normalization.track_contents WHERE source_track_uuid=@trackUuid AND NOT retracted)",
                        new { trackUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                if (!exists)
                {
                    return null;
                }
                var rows = (
                    await c.QueryAsync<NormalizedTrackContent>(
                        new CommandDefinition(
                            // Catalog moves and corrected setlists can invalidate an occurrence alignment without changing
                            // the file's contents or its retained evidence. Hide that link in the current
                            // projection; do not rewrite the decision or move the recording implicitly.
                            "SELECT x.uuid,x.source_track_uuid,x.position,x.kind,x.original_text,x.repertoire_song_uuid,"
                                + MusicRevisionColumns
                                + """
                                ,CASE
                                    WHEN occurrence.uuid IS NULL OR edition.uuid IS NULL THEN NULL
                                    WHEN x.repertoire_song_uuid IS NOT NULL AND occurrence.repertoire_song_uuid IS NOT NULL
                                        AND x.repertoire_song_uuid<>occurrence.repertoire_song_uuid THEN NULL
                                    WHEN track.uuid IS NULL THEN x.occurrence_uuid
                                    WHEN source.uuid IS DISTINCT FROM x.source_uuid
                                        OR artist.uuid IS DISTINCT FROM x.artist_uuid
                                        OR track.artist_id IS DISTINCT FROM source.artist_id
                                        OR sets.id IS NULL OR edition.artist_uuid IS DISTINCT FROM artist.uuid THEN NULL
                                    WHEN edition.show_uuid IS NOT NULL AND edition.show_uuid IS DISTINCT FROM show.uuid THEN NULL
                                    ELSE x.occurrence_uuid
                                END AS occurrence_uuid,
                                x.start_ms,x.end_ms,x.match_evidence,
                                COALESCE((show.uuid IS NOT NULL AND source.uuid=x.source_uuid AND artist.uuid=x.artist_uuid
                                    AND track.artist_id=source.artist_id AND sets.id IS NOT NULL
                                    AND NOT track.is_orphaned AND NULLIF(trim(track.mp3_url),'') IS NOT NULL),false) AS reference_available
                                FROM normalization.track_contents x
                                JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                                LEFT JOIN normalization.song_occurrences occurrence ON occurrence.uuid=x.occurrence_uuid AND NOT occurrence.retracted
                                LEFT JOIN normalization.setlist_editions edition ON edition.uuid=occurrence.edition_uuid AND NOT edition.retracted
                                LEFT JOIN source_tracks track ON track.uuid=x.source_track_uuid
                                LEFT JOIN sources source ON source.id=track.source_id
                                LEFT JOIN artists artist ON artist.id=source.artist_id
                                LEFT JOIN source_sets sets ON sets.id=track.source_set_id AND sets.source_id=source.id
                                LEFT JOIN shows show ON show.id=source.show_id AND show.artist_id=source.artist_id
                                WHERE x.source_track_uuid=@trackUuid AND NOT x.retracted AND (@afterUuid::uuid IS NULL OR (x.position,x.uuid)>(@position,@afterUuid))
                                ORDER BY x.position,x.uuid LIMIT @take
                                """,
                            new
                            {
                                trackUuid,
                                afterUuid = after?.Uuid,
                                position,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                var availableSongs = await AvailablePassageSongsAsync(
                    c,
                    t,
                    rows.Select(x => x.repertoire_song_uuid),
                    cancellationToken
                );
                foreach (var row in rows)
                {
                    if (row.repertoire_song_uuid is Guid song && !availableSongs.Contains(song))
                    {
                        row.repertoire_song_uuid = null;
                    }
                }
                await PopulateMusicEvidenceAsync(c, t, rows, cancellationToken);
                return Page(
                    rows,
                    limit,
                    x =>
                        EncodeCursor(
                            "track_contents",
                            trackUuid,
                            null,
                            "position_uuid",
                            x.position.ToString(CultureInfo.InvariantCulture),
                            x.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    // Resolve only the current page's references. Redirected UUIDs remain useful public links
    // while their survivor exists; withdrawal keeps the historical passage but removes its link.
    private static async Task<HashSet<Guid>> AvailablePassageSongsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IEnumerable<Guid?> references,
        CancellationToken ct
    )
    {
        var requested = references
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
        if (requested.Length == 0)
        {
            return [];
        }
        return (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    WITH RECURSIVE identities AS (
                        SELECT uuid AS requested_uuid,uuid,redirect_uuid
                        FROM normalization.repertoire_songs WHERE uuid=ANY(@requested) AND NOT retracted
                        UNION
                        SELECT i.requested_uuid,song.uuid,song.redirect_uuid
                        FROM identities i JOIN normalization.repertoire_songs song ON song.uuid=i.redirect_uuid
                        WHERE NOT song.retracted
                    )
                    SELECT DISTINCT requested_uuid FROM identities WHERE redirect_uuid IS NULL
                    """,
                    new { requested },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();
    }
}
