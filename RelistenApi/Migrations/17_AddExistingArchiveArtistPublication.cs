using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Separates explicit existing-artist approval from ownership of an intake-created artist.</summary>
[Migration(17, "Support reviewed Archive publication for existing artists")]
public sealed class AddExistingArchiveArtistPublication : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.archive_artist_candidates
            ADD COLUMN is_existing_artist boolean NOT NULL DEFAULT false,
            ADD CONSTRAINT archive_existing_artist_mapped CHECK (NOT is_existing_artist OR artist_uuid IS NOT NULL);

        CREATE OR REPLACE VIEW collection_playable_sources AS
        WITH inclusions AS (
            -- Keep the accepted reference through metadata conflicts, but require its current
            -- performer/item/provider identity. Sources alone have no provider column.
            SELECT m.collection_uuid, COALESCE(m.source_uuid,r.canonical_source_uuid) AS source_uuid
            FROM normalization.collection_recording_memberships m
            LEFT JOIN normalization.archive_item_resolutions r ON r.item_identifier=m.item_identifier
            WHERE m.active AND (m.basis='editorial' OR EXISTS (
                SELECT 1 FROM sources accepted JOIN artists performer ON performer.id=accepted.artist_id
                WHERE accepted.uuid=r.canonical_source_uuid AND performer.uuid=r.selected_artist_uuid
                    AND accepted.upstream_identifier=r.item_identifier
                    AND EXISTS (
                        SELECT 1 FROM links l JOIN upstream_sources provider ON provider.id=l.upstream_source_id
                        WHERE l.source_id=accepted.id AND l.for_source AND provider.name='archive.org'
                    )
            ))
            UNION
            SELECT m.collection_uuid,s.uuid FROM normalization.collection_artist_memberships m
            JOIN artists a ON a.uuid=m.artist_uuid JOIN sources s ON s.artist_id=a.id WHERE m.active
        )
        SELECT i.collection_uuid,s.uuid AS source_uuid,a.uuid AS artist_uuid,a.name AS artist_name,
            a.slug AS artist_slug,COALESCE(a.sort_name,a.name) AS artist_sort_name,
            sh.uuid AS show_uuid,sh.date AS show_date,s.display_date,
            tracks.track_count,tracks.duration
        FROM inclusions i JOIN sources s ON s.uuid=i.source_uuid JOIN artists a ON a.id=s.artist_id
        JOIN shows sh ON sh.id=s.show_id AND sh.artist_id=s.artist_id
        JOIN LATERAL (
            SELECT count(*) AS track_count,COALESCE(sum(t.duration),0)::double precision AS duration
            FROM source_tracks t JOIN source_sets sets ON sets.id=t.source_set_id AND sets.source_id=s.id
            WHERE t.source_id=s.id AND t.artist_id=s.artist_id AND NOT t.is_orphaned
                AND NULLIF(trim(t.mp3_url),'') IS NOT NULL
        ) tracks ON tracks.track_count>0
        -- Existing artists retain their native visibility policy. Intake-created artists
        -- remain hidden until their first successful publication/rebuild acknowledgement.
        WHERE NOT ((a.featured & 2)<>0 AND EXISTS (
            SELECT 1 FROM normalization.archive_artist_candidates c
            WHERE c.artist_uuid=a.uuid AND NOT c.is_existing_artist
        ));
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep existing-artist ownership decisions on rollback. Stop intake writers before running older code."
        );
}
