using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Separates browsing membership and shared item resolution from canonical catalog ownership.</summary>
[Migration(14, "Add Relisten collections and recording memberships")]
public sealed class AddRelistenCollections : Migration
{
    public const string SchemaSql = """
        CREATE TABLE collections (
            uuid uuid PRIMARY KEY, slug text NOT NULL UNIQUE, name text NOT NULL,
            description text NOT NULL DEFAULT '', published boolean NOT NULL DEFAULT false,
            created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE normalization.collection_selectors (
            uuid uuid PRIMARY KEY, collection_uuid uuid NOT NULL REFERENCES collections(uuid),
            upstream_source_id integer NOT NULL REFERENCES upstream_sources(id), upstream_identifier text NOT NULL,
            enabled boolean NOT NULL DEFAULT true, scan_id uuid,
            scan_state text NOT NULL DEFAULT 'idle' CHECK(scan_state IN ('idle','running','complete')),
            cursor text, completed_at timestamptz, last_error text,
            search_complete boolean NOT NULL DEFAULT false,
            seen_cursors text[] NOT NULL DEFAULT '{}',
            discovered_count bigint NOT NULL DEFAULT 0 CHECK(discovered_count>=0),
            pending_items text[] NOT NULL DEFAULT '{}',
            created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE(collection_uuid,upstream_source_id,upstream_identifier)
        );
        CREATE TABLE normalization.collection_artist_memberships (
            collection_uuid uuid NOT NULL REFERENCES collections(uuid), artist_uuid uuid NOT NULL,
            active boolean NOT NULL, review_basis text NOT NULL, reason text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
            retired_at timestamptz, PRIMARY KEY(collection_uuid,artist_uuid)
        );
        CREATE TABLE normalization.archive_item_resolutions (
            item_identifier text PRIMARY KEY,
            observation_id uuid REFERENCES normalization.archive_item_observations(observation_id),
            accepted_observation_id uuid REFERENCES normalization.archive_item_observations(observation_id),
            selected_artist_uuid uuid, canonical_source_uuid uuid,
            decision_basis text NOT NULL DEFAULT '',
            status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','resolved','conflict','missing')),
            reason text, last_error text, last_attempt_at timestamptz,
            created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
            CHECK(canonical_source_uuid IS NULL OR selected_artist_uuid IS NOT NULL)
        );
        CREATE TABLE normalization.collection_recording_memberships (
            membership_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            collection_uuid uuid NOT NULL REFERENCES collections(uuid),
            basis text NOT NULL CHECK(basis IN ('editorial','provider')),
            source_uuid uuid, selector_uuid uuid REFERENCES normalization.collection_selectors(uuid),
            item_identifier text REFERENCES normalization.archive_item_resolutions(item_identifier),
            active boolean NOT NULL, review_basis text NOT NULL, reason text NOT NULL, last_seen_scan_id uuid,
            created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), retired_at timestamptz,
            CHECK((basis='editorial' AND source_uuid IS NOT NULL AND selector_uuid IS NULL AND item_identifier IS NULL)
                OR (basis='provider' AND source_uuid IS NULL AND selector_uuid IS NOT NULL AND item_identifier IS NOT NULL))
        );
        CREATE UNIQUE INDEX collection_editorial_recording ON normalization.collection_recording_memberships(collection_uuid,source_uuid)
            WHERE basis='editorial';
        CREATE UNIQUE INDEX collection_provider_recording ON normalization.collection_recording_memberships(selector_uuid,item_identifier)
            WHERE basis='provider';
        CREATE INDEX collection_active_recordings ON normalization.collection_recording_memberships(collection_uuid)
            WHERE active;
        CREATE INDEX collection_active_artists ON normalization.collection_artist_memberships(artist_uuid,collection_uuid)
            WHERE active;
        CREATE INDEX collection_resolution_pending ON normalization.archive_item_resolutions(item_identifier)
            WHERE status<>'resolved';

        -- Canonical UUID references deliberately have no catalog foreign keys. Native source/artist
        -- deletion must retain membership and evidence. Inner joins hide unavailable references.
        CREATE VIEW collection_playable_sources AS
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
        -- Native AutoCreated artists may be explicitly selected. Any mapped intake candidate
        -- retains its cohort membership even when its diagnostic status becomes needs_review.
        -- Its first reveal must still wait for successful publication/rebuild acknowledgement.
        WHERE NOT ((a.featured & 2)<>0 AND EXISTS (
            SELECT 1 FROM normalization.archive_artist_candidates c
            WHERE c.artist_uuid=a.uuid
        ));
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep collection membership and item decisions on rollback. Removal requires a reviewed data migration."
        );
}
