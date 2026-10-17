using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Fences catalog associations independently of retained provider revisions. Clocks survive
/// catalog deletion and rollback never consumes a generation. Generations are opaque counters,
/// not transaction counts. Publications lock clocks before evidence and never write catalog rows.
/// </summary>
[Migration(24, "Fence normalization catalog bindings and contributed inputs")]
public sealed class AddNormalizationCatalogBindings : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.catalog_artist_revisions (
            artist_uuid uuid PRIMARY KEY,
            generation bigint NOT NULL DEFAULT 0 CHECK(generation>=0)
        );
        CREATE TABLE normalization.catalog_source_identity_revisions (
            upstream_identifier text PRIMARY KEY CHECK(length(trim(upstream_identifier))>0),
            generation bigint NOT NULL DEFAULT 0 CHECK(generation>=0)
        );
        INSERT INTO normalization.catalog_artist_revisions(artist_uuid) SELECT uuid FROM artists;
        INSERT INTO normalization.catalog_source_identity_revisions(upstream_identifier)
            SELECT upstream_identifier FROM sources WHERE length(trim(upstream_identifier))>0
            UNION SELECT item_identifier FROM normalization.archive_item_intake WHERE length(trim(item_identifier))>0
            UNION SELECT item_identifier FROM normalization.archive_item_resolutions WHERE length(trim(item_identifier))>0;

        CREATE TABLE normalization.work_catalog_inputs (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            artist_uuid uuid NOT NULL REFERENCES normalization.catalog_artist_revisions(artist_uuid),
            published_generation bigint,
            PRIMARY KEY(subject_uuid,artist_uuid)
        );
        CREATE TABLE normalization.work_source_identity_inputs (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            upstream_identifier text NOT NULL REFERENCES normalization.catalog_source_identity_revisions(upstream_identifier),
            published_generation bigint,
            PRIMARY KEY(subject_uuid,upstream_identifier)
        );

        -- Empty contribution headers retain their revision: removal is durable input, not absence
        -- of a receipt. Catalog keys intentionally have no FK to rebuildable public rows.
        CREATE TABLE normalization.work_input_contributions (
            uuid uuid PRIMARY KEY,
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            contributor_provider text NOT NULL CHECK(length(trim(contributor_provider))>0),
            contributor_type text NOT NULL CHECK(length(trim(contributor_type))>0),
            contributor_key text NOT NULL CHECK(length(trim(contributor_key))>0),
            revision bigint NOT NULL CHECK(revision>0),
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(subject_uuid,contributor_provider,contributor_type,contributor_key)
        );
        CREATE TABLE normalization.work_contribution_inputs (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            resource_uuid uuid NOT NULL REFERENCES normalization.input_resources(uuid),
            PRIMARY KEY(contribution_uuid,resource_uuid)
        );
        CREATE TABLE normalization.work_contribution_dependencies (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            dependency_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            PRIMARY KEY(contribution_uuid,dependency_uuid)
        );
        CREATE TABLE normalization.work_contribution_catalog_inputs (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            artist_uuid uuid NOT NULL,
            PRIMARY KEY(contribution_uuid,artist_uuid)
        );
        CREATE TABLE normalization.work_contribution_source_identities (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            upstream_identifier text NOT NULL CHECK(length(trim(upstream_identifier))>0),
            PRIMARY KEY(contribution_uuid,upstream_identifier)
        );

        CREATE FUNCTION normalization.bump_catalog_revisions(artist_keys uuid[],source_keys text[]) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE artist_key uuid; source_key text;
        BEGIN
            -- A catalog transaction commonly changes several sources for one artist. Lock that
            -- artist before any source identity, just as normalization publication does.
            FOR artist_key IN SELECT DISTINCT key FROM unnest(artist_keys) key WHERE key IS NOT NULL ORDER BY key LOOP
                INSERT INTO normalization.catalog_artist_revisions(artist_uuid,generation) VALUES(artist_key,1)
                    ON CONFLICT(artist_uuid) DO UPDATE SET generation=catalog_artist_revisions.generation+1;
            END LOOP;
            FOR source_key IN SELECT DISTINCT key COLLATE "C" FROM unnest(source_keys) key
                    WHERE key IS NOT NULL AND length(trim(key))>0 ORDER BY key COLLATE "C" LOOP
                INSERT INTO normalization.catalog_source_identity_revisions(upstream_identifier,generation) VALUES(source_key,1)
                    ON CONFLICT(upstream_identifier) DO UPDATE SET generation=catalog_source_identity_revisions.generation+1;
            END LOOP;
        END $$;

        CREATE FUNCTION normalization.record_catalog_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            old_row jsonb:='{}'; new_row jsonb:='{}'; row_value jsonb;
            ignored text[]:=ARRAY['created_at','updated_at'];
            artist_ids integer[]:='{}'; artist_keys uuid[]:='{}'; source_keys text[]:='{}';
            source_ids bigint[]:='{}'; set_ids bigint[]:='{}'; show_ids bigint[]:='{}';
            setlist_ids bigint[]:='{}'; song_ids bigint[]:='{}'; venue_ids bigint[]:='{}';
            upstream_ids integer[]:='{}'; source_uuids uuid[]:='{}'; candidate_ids uuid[]:='{}';
        BEGIN
            IF TG_OP<>'INSERT' THEN old_row:=to_jsonb(OLD); END IF;
            IF TG_OP<>'DELETE' THEN new_row:=to_jsonb(NEW); END IF;
            IF TG_TABLE_NAME='sources' THEN
                ignored:=ignored||ARRAY['avg_rating','avg_rating_weighted','num_reviews','num_ratings'];
            ELSIF TG_TABLE_NAME='shows' THEN
                ignored:=ignored||ARRAY['avg_rating'];
            ELSIF TG_TABLE_NAME='archive_item_intake' THEN
                ignored:=ignored||ARRAY['last_observed_at','last_attempt_at','discovered_at','last_discovered_at',
                    'attempts','next_attempt_at','last_error','rebuilt_revision'];
            ELSIF TG_TABLE_NAME='archive_item_resolutions' THEN
                ignored:=ignored||ARRAY['last_attempt_at','last_error'];
            END IF;
            IF TG_OP='UPDATE' AND (old_row-ignored) IS NOT DISTINCT FROM (new_row-ignored) THEN RETURN NULL; END IF;

            FOREACH row_value IN ARRAY ARRAY[old_row,new_row] LOOP
                IF row_value='{}'::jsonb THEN CONTINUE; END IF;
                IF row_value->>'artist_id' IS NOT NULL THEN
                    artist_ids:=array_append(artist_ids,(row_value->>'artist_id')::integer);
                END IF;
                IF TG_TABLE_NAME='artists' THEN
                    artist_keys:=array_append(artist_keys,(row_value->>'uuid')::uuid);
                    artist_ids:=array_append(artist_ids,(row_value->>'id')::integer);
                ELSIF TG_TABLE_NAME='sources' THEN
                    source_ids:=array_append(source_ids,(row_value->>'id')::bigint);
                    source_keys:=array_append(source_keys,row_value->>'upstream_identifier');
                ELSIF TG_TABLE_NAME IN ('source_sets','source_tracks','links','source_show_assignments') THEN
                    source_ids:=array_append(source_ids,(row_value->>'source_id')::bigint);
                    IF TG_TABLE_NAME='source_tracks' THEN
                        set_ids:=array_append(set_ids,(row_value->>'source_set_id')::bigint);
                    END IF;
                ELSIF TG_TABLE_NAME='setlist_songs_plays' THEN
                    setlist_ids:=array_append(setlist_ids,(row_value->>'played_setlist_show_id')::bigint);
                    song_ids:=array_append(song_ids,(row_value->>'played_setlist_song_id')::bigint);
                ELSIF TG_TABLE_NAME='setlist_show_assignments' THEN
                    setlist_ids:=array_append(setlist_ids,(row_value->>'setlist_show_id')::bigint);
                ELSIF TG_TABLE_NAME='venues' THEN
                    venue_ids:=array_append(venue_ids,(row_value->>'id')::bigint);
                ELSIF TG_TABLE_NAME='upstream_sources' THEN
                    upstream_ids:=array_append(upstream_ids,(row_value->>'id')::integer);
                ELSIF TG_TABLE_NAME='archive_artist_candidates' THEN
                    candidate_ids:=array_append(candidate_ids,(row_value->>'candidate_id')::uuid);
                END IF;
                IF TG_TABLE_NAME IN ('source_show_assignments','setlist_show_assignments','show_performance_keys') THEN
                    show_ids:=array_append(show_ids,(row_value->>'show_id')::bigint);
                END IF;
                IF TG_TABLE_NAME IN ('archive_item_intake','archive_item_resolutions','archive_artist_candidates') THEN
                    artist_keys:=artist_keys||ARRAY[(row_value->>'published_artist_uuid')::uuid,
                        (row_value->>'selected_artist_uuid')::uuid,(row_value->>'artist_uuid')::uuid];
                    source_uuids:=source_uuids||ARRAY[(row_value->>'published_source_uuid')::uuid,
                        (row_value->>'canonical_source_uuid')::uuid];
                    candidate_ids:=candidate_ids||ARRAY[(row_value->>'candidate_id')::uuid,
                        (row_value->>'performer_candidate_id')::uuid];
                    source_keys:=array_append(source_keys,row_value->>'item_identifier');
                END IF;
            END LOOP;

            IF cardinality(set_ids)>0 THEN
                SELECT source_ids||COALESCE(array_agg(source_id),'{}') INTO source_ids FROM source_sets WHERE id=ANY(set_ids);
            END IF;
            IF cardinality(song_ids)>0 THEN
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}') INTO artist_ids FROM setlist_songs WHERE id=ANY(song_ids);
            END IF;
            IF cardinality(setlist_ids)>0 THEN
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}') INTO artist_ids FROM setlist_shows WHERE id=ANY(setlist_ids);
            END IF;
            IF cardinality(show_ids)>0 THEN
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}') INTO artist_ids FROM shows WHERE id=ANY(show_ids);
            END IF;
            IF cardinality(venue_ids)>0 THEN
                -- A legacy venue can be shared or have no artist_id. Fence its actual catalog users.
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}') INTO artist_ids FROM (
                    SELECT artist_id FROM sources WHERE venue_id=ANY(venue_ids)
                    UNION SELECT artist_id FROM shows WHERE venue_id=ANY(venue_ids)
                    UNION SELECT artist_id FROM setlist_shows WHERE venue_id=ANY(venue_ids)
                ) venue_artists;
            END IF;
            IF cardinality(upstream_ids)>0 THEN
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}') INTO artist_ids
                    FROM artists_upstream_sources WHERE upstream_source_id=ANY(upstream_ids);
                SELECT source_ids||COALESCE(array_agg(source_id),'{}') INTO source_ids FROM links WHERE upstream_source_id=ANY(upstream_ids);
            END IF;
            IF cardinality(candidate_ids)>0 THEN
                SELECT artist_keys||COALESCE(array_agg(artist_uuid),'{}') INTO artist_keys
                    FROM normalization.archive_artist_candidates WHERE candidate_id=ANY(candidate_ids);
                SELECT source_keys||COALESCE(array_agg(item_identifier),'{}'),
                    artist_keys||COALESCE(array_agg(artist_uuid),'{}') INTO source_keys,artist_keys
                FROM (
                    SELECT item_identifier,published_artist_uuid AS artist_uuid FROM normalization.archive_item_intake WHERE candidate_id=ANY(candidate_ids)
                    UNION SELECT item_identifier,selected_artist_uuid FROM normalization.archive_item_resolutions WHERE performer_candidate_id=ANY(candidate_ids)
                ) candidate_items;
            END IF;
            IF TG_TABLE_NAME IN ('archive_item_intake','archive_item_resolutions','archive_artist_candidates') THEN
                -- Shared item decisions can invalidate every known catalog candidate. Ordinary
                -- source writes fence only their owners; the global item clock handles new rivals.
                SELECT source_ids||COALESCE(array_agg(id),'{}') INTO source_ids FROM sources
                    WHERE uuid=ANY(source_uuids) OR upstream_identifier=ANY(source_keys);
            END IF;
            IF TG_TABLE_NAME IN ('artists','artists_upstream_sources','upstream_sources') THEN
                SELECT source_ids||COALESCE(array_agg(id),'{}') INTO source_ids FROM sources WHERE artist_id=ANY(artist_ids);
            END IF;
            IF cardinality(source_ids)>0 THEN
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}') INTO artist_ids FROM sources WHERE id=ANY(source_ids);
                IF TG_TABLE_NAME IN ('artists','sources','links','artists_upstream_sources','upstream_sources',
                    'archive_item_intake','archive_item_resolutions','archive_artist_candidates') THEN
                    SELECT source_keys||COALESCE(array_agg(upstream_identifier),'{}') INTO source_keys FROM sources WHERE id=ANY(source_ids);
                END IF;
            END IF;
            IF cardinality(artist_ids)>0 THEN
                SELECT artist_keys||COALESCE(array_agg(uuid),'{}') INTO artist_keys FROM artists WHERE id=ANY(artist_ids);
            END IF;
            -- On cascading deletes a child may no longer see its parent. The parent's own trigger
            -- fences that deletion using OLD identifiers; retained clocks deliberately have no FK.
            PERFORM normalization.bump_catalog_revisions(artist_keys,source_keys);
            RETURN NULL;
        END $$;

        CREATE FUNCTION normalization.fence_artist_catalog_delete() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- AFTER child callbacks cannot join an already deleted artist. Take the artist clock
            -- before cascading deletion can acquire source-identity clocks without that parent.
            PERFORM normalization.bump_catalog_revisions(ARRAY[OLD.uuid],
                ARRAY(SELECT upstream_identifier FROM sources WHERE artist_id=OLD.id));
            RETURN OLD;
        END $$;
        CREATE TRIGGER normalization_catalog_artist_delete BEFORE DELETE ON artists
            FOR EACH ROW EXECUTE FUNCTION normalization.fence_artist_catalog_delete();

        CREATE FUNCTION normalization.record_catalog_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- TRUNCATE and schema changes require paused writers/publications: their relation locks
            -- precede this callback. Preserve clocks even after all public identities disappear.
            PERFORM normalization.bump_catalog_revisions(
                ARRAY(SELECT artist_uuid FROM normalization.catalog_artist_revisions),
                ARRAY(SELECT upstream_identifier FROM normalization.catalog_source_identity_revisions));
            RETURN NULL;
        END $$;

        DO $$ DECLARE relation text; BEGIN
            FOREACH relation IN ARRAY ARRAY['public.artists','public.artists_upstream_sources','public.features',
                'public.sources','public.source_sets','public.source_tracks','public.shows','public.venues','public.links',
                'public.setlist_shows','public.setlist_songs','public.setlist_songs_plays','public.show_performance_keys',
                'public.source_show_assignments','public.setlist_show_assignments',
                'public.upstream_sources','normalization.archive_item_intake','normalization.archive_item_resolutions',
                'normalization.archive_artist_candidates'] LOOP
                EXECUTE format('CREATE TRIGGER normalization_catalog_revision AFTER INSERT OR UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION normalization.record_catalog_revision()',relation);
                EXECUTE format('CREATE TRIGGER normalization_catalog_truncate AFTER TRUNCATE ON %s FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_catalog_truncate()',relation);
            END LOOP;
        END $$;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization publication on rollback; retain catalog clocks and contribution revisions."
        );
}
