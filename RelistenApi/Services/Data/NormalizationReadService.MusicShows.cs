using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    /// <summary>
    /// Finds playable engagements supported by any accepted setlist account or file content.
    /// An edition need not be selected: selection chooses one display account, not which evidence exists.
    /// </summary>
    public Task<NormalizationPage<NormalizedShowReference>?> ListSongShowsAsync(
        Guid songUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    ) => ListMusicShowsAsync(songUuid, true, null, limit, cursor, cancellationToken);

    /// <summary>Finds engagements across accepted repertoire links to one global work.</summary>
    public Task<NormalizationPage<NormalizedShowReference>?> ListCompositionShowsAsync(
        Guid compositionUuid,
        Guid? artistUuid = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    ) => ListMusicShowsAsync(compositionUuid, false, artistUuid, limit, cursor, cancellationToken);

    private Task<NormalizationPage<NormalizedShowReference>?> ListMusicShowsAsync(
        Guid uuid,
        bool repertoire,
        Guid? artistUuid,
        int limit,
        string? cursor,
        CancellationToken cancellationToken
    )
    {
        var route = repertoire ? "song_shows" : "composition_shows";
        var filter = artistUuid?.ToString("D");
        var after = ParseCursor(cursor, route, uuid, filter, "date_desc_uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveMusicAsync(c, t, uuid, repertoire, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                await RequireArtistAsync(c, t, artistUuid, cancellationToken);
                var roots = repertoire ? SongShowRoots : CompositionShowRoots;
                var rows = (
                    await c.QueryAsync<NormalizedShowReference>(
                        new CommandDefinition(
                            "WITH RECURSIVE playable AS ("
                                + PlayableSources
                                + MusicPlaybackScope
                                + "),"
                                + roots
                                + MusicShowMatches,
                            new
                            {
                                survivor,
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
                    show =>
                        EncodeCursor(
                            route,
                            uuid,
                            filter,
                            "date_desc_uuid",
                            show.display_date,
                            show.show_uuid
                        )
                );
            },
            cancellationToken
        );
    }

    // Restrict the shared playback predicate before its per-source track aggregate. A work with
    // a few supported recordings must not aggregate the complete catalog on every request.
    private const string MusicPlaybackScope = """
        AND (EXISTS(SELECT 1 FROM candidate_file_ids candidate
            JOIN normalization.track_contents content ON content.uuid=candidate.uuid WHERE content.source_uuid=s.uuid)
            OR EXISTS(SELECT 1 FROM supported_occurrences candidate WHERE candidate.show_uuid=sh.uuid))
        """;

    private const string SongShowRoots = """
        song_roots(uuid,bases) AS (SELECT @survivor::uuid,ARRAY[]::text[]),
        """;

    private const string CompositionShowRoots = """
        work_ids(uuid) AS (
            SELECT @survivor::uuid
            UNION SELECT c.uuid FROM normalization.compositions c JOIN work_ids parent ON c.redirect_uuid=parent.uuid WHERE NOT c.retracted
        ),
        linked_songs(uuid,bases) AS (
            SELECT link.repertoire_song_uuid,ARRAY[revision.basis]
            FROM normalization.repertoire_compositions link
            JOIN work_ids work ON work.uuid=link.composition_uuid
            JOIN normalization.repertoire_songs song ON song.uuid=link.repertoire_song_uuid AND NOT song.retracted
            JOIN normalization.music_revisions revision ON revision.uuid=link.revision_uuid
            WHERE NOT link.retracted
            UNION SELECT target.uuid,linked.bases FROM linked_songs linked
                JOIN normalization.repertoire_songs song ON song.uuid=linked.uuid
                JOIN normalization.repertoire_songs target ON target.uuid=song.redirect_uuid AND NOT target.retracted
        ),
        song_roots(uuid,bases) AS (
            SELECT linked.uuid,linked.bases FROM linked_songs linked
            JOIN normalization.repertoire_songs song ON song.uuid=linked.uuid AND song.redirect_uuid IS NULL
        ),
        """;

    // UUID references survive merges. Walk accepted ancestors rather than rewriting historical
    // occurrences or file content. File claims use their exact current track/set/source joins;
    // another playable track in the source cannot rescue an orphaned or missing claimed file.
    private const string MusicShowMatches = """
        matched_songs(uuid,bases) AS (
            SELECT s.uuid,roots.bases || ARRAY[r.basis]
            FROM song_roots roots JOIN normalization.repertoire_songs s ON s.uuid=roots.uuid AND NOT s.retracted
            JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid
            UNION SELECT s.uuid,parent.bases || ARRAY[r.basis]
            FROM matched_songs parent JOIN normalization.repertoire_songs s ON s.redirect_uuid=parent.uuid AND NOT s.retracted
            JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid
        ),
        supported_occurrences AS (
            SELECT sh.uuid AS show_uuid,'performance_occurrence'::text AS association_kind,
                ARRAY[CASE WHEN e.ordered THEN 'ordered_setlist' ELSE 'unordered_membership' END] AS evidence,
                song.bases || ARRAY[occurrence_revision.basis,edition_revision.basis] AS bases
            FROM normalization.song_occurrences o
            JOIN matched_songs song ON song.uuid=o.repertoire_song_uuid
            JOIN normalization.repertoire_songs identity ON identity.uuid=o.repertoire_song_uuid
            JOIN normalization.setlist_editions e ON e.uuid=o.edition_uuid AND NOT e.retracted
                AND e.artist_uuid=identity.artist_uuid
            JOIN artists a ON a.uuid=e.artist_uuid
            JOIN shows sh ON sh.uuid=e.show_uuid AND sh.artist_id=a.id
            JOIN normalization.music_revisions occurrence_revision ON occurrence_revision.uuid=o.revision_uuid
            JOIN normalization.music_revisions edition_revision ON edition_revision.uuid=e.revision_uuid
            WHERE NOT o.retracted
        ),
        candidate_file_ids(uuid) AS (
            SELECT content.uuid FROM normalization.track_contents content
                JOIN matched_songs song ON song.uuid=content.repertoire_song_uuid WHERE NOT content.retracted
            UNION SELECT content.uuid FROM normalization.track_contents content
                JOIN normalization.song_occurrences occurrence ON occurrence.uuid=content.occurrence_uuid AND NOT occurrence.retracted
                JOIN matched_songs song ON song.uuid=occurrence.repertoire_song_uuid WHERE NOT content.retracted
        ),
        current_files AS (
            SELECT content.*,p.show_uuid AS primary_show_uuid,p.artist_uuid AS current_artist_uuid
            FROM candidate_file_ids candidate JOIN normalization.track_contents content ON content.uuid=candidate.uuid
            JOIN source_tracks track ON track.uuid=content.source_track_uuid AND NOT track.is_orphaned
                AND NULLIF(trim(track.mp3_url),'') IS NOT NULL
            JOIN sources source ON source.id=track.source_id AND source.uuid=content.source_uuid
                AND source.artist_id=track.artist_id
            JOIN source_sets sets ON sets.id=track.source_set_id AND sets.source_id=source.id
            JOIN playable p ON p.source_uuid=source.uuid AND p.artist_uuid=content.artist_uuid
            WHERE NOT content.retracted
        ),
        -- A recording belongs to one performance. Stale or inconsistent occurrence links must
        -- not make its files advertise playback under another early/late engagement. If an
        -- occurrence's song changes independently, retain the file title but withhold its old
        -- alignment. Different stored song IDs require refreshed alignment, even after a merge.
        aligned_files AS (
            SELECT f.*,o.repertoire_song_uuid AS occurrence_song_uuid,
                o.revision_uuid AS occurrence_revision_uuid,e.revision_uuid AS edition_revision_uuid,
                e.show_uuid AS occurrence_show_uuid
            FROM current_files f
            LEFT JOIN normalization.song_occurrences o ON o.uuid=f.occurrence_uuid AND NOT o.retracted
            LEFT JOIN normalization.setlist_editions e ON e.uuid=o.edition_uuid AND NOT e.retracted
                AND e.artist_uuid=f.current_artist_uuid
                AND (e.show_uuid IS NULL OR e.show_uuid=f.primary_show_uuid)
                AND (f.repertoire_song_uuid IS NULL OR o.repertoire_song_uuid IS NULL OR f.repertoire_song_uuid=o.repertoire_song_uuid)
        ),
        occurrence_matches AS (
            SELECT supported.show_uuid,supported.association_kind,supported.evidence,supported.bases
            FROM supported_occurrences supported
            WHERE EXISTS(SELECT 1 FROM playable available WHERE available.show_uuid=supported.show_uuid)
        ),
        file_matches AS (
            SELECT f.primary_show_uuid AS show_uuid,
                CASE WHEN f.occurrence_show_uuid IS NOT NULL THEN 'performance_occurrence' ELSE 'recording_primary_show' END AS association_kind,
                ARRAY[f.match_evidence] AS evidence,
                song.bases || ARRAY[file_revision.basis] ||
                    CASE WHEN f.edition_revision_uuid IS NOT NULL THEN ARRAY[occurrence_revision.basis,edition_revision.basis] ELSE ARRAY[]::text[] END AS bases
            FROM aligned_files f
            JOIN matched_songs song ON song.uuid=COALESCE(f.repertoire_song_uuid,
                CASE WHEN f.edition_revision_uuid IS NOT NULL THEN f.occurrence_song_uuid END)
            JOIN normalization.repertoire_songs identity ON identity.uuid=song.uuid AND identity.artist_uuid=f.current_artist_uuid
            JOIN normalization.music_revisions file_revision ON file_revision.uuid=f.revision_uuid
            LEFT JOIN normalization.music_revisions occurrence_revision ON occurrence_revision.uuid=f.occurrence_revision_uuid
            LEFT JOIN normalization.music_revisions edition_revision ON edition_revision.uuid=f.edition_revision_uuid
        ),
        matches AS (SELECT * FROM occurrence_matches UNION ALL SELECT * FROM file_matches),
        grouped AS (
            SELECT show_uuid,bool_or(association_kind='performance_occurrence') AS occurrence_association,
                array_agg(DISTINCT evidence_value ORDER BY evidence_value) AS match_evidence,
                array_agg(DISTINCT basis_value ORDER BY basis_value) AS decision_basis
            FROM matches CROSS JOIN LATERAL unnest(evidence) evidence_value CROSS JOIN LATERAL unnest(bases) basis_value
            GROUP BY show_uuid
        )
        SELECT sh.uuid AS show_uuid,a.uuid AS artist_uuid,sh.display_date,sh.performance_type,sh.performance_label,
            grouped.match_evidence,grouped.decision_basis,
            CASE WHEN grouped.occurrence_association THEN 'performance_occurrence' ELSE 'recording_primary_show' END AS association_kind
        FROM grouped JOIN shows sh ON sh.uuid=grouped.show_uuid JOIN artists a ON a.id=sh.artist_id
        WHERE (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid)
            AND (@afterUuid::uuid IS NULL OR sh.display_date COLLATE "C"<@sort COLLATE "C" OR (sh.display_date=@sort AND sh.uuid>@afterUuid))
        ORDER BY sh.display_date COLLATE "C" DESC,sh.uuid LIMIT @take
        """;
}
