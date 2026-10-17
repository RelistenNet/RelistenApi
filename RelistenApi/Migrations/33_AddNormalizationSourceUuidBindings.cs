using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// A recording can be requested before it exists. Retained UUID clocks fence exact lookup
/// absence and reuse; the shared catalog callback preserves ordering for related mutations.
/// </summary>
[Migration(33, "Fence negative recording lookups by source UUID")]
public sealed class AddNormalizationSourceUuidBindings : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.catalog_source_uuid_revisions (
            source_uuid uuid PRIMARY KEY CHECK(source_uuid<>'00000000-0000-0000-0000-000000000000'::uuid),
            generation bigint NOT NULL DEFAULT 0 CHECK(generation>=0)
        );
        INSERT INTO normalization.catalog_source_uuid_revisions(source_uuid)
            SELECT uuid FROM sources WHERE uuid IS NOT NULL AND uuid<>'00000000-0000-0000-0000-000000000000'::uuid;
        CREATE TABLE normalization.work_source_uuid_inputs (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            source_uuid uuid NOT NULL REFERENCES normalization.catalog_source_uuid_revisions(source_uuid),
            published_generation bigint,
            PRIMARY KEY(subject_uuid,source_uuid)
        );
        CREATE TABLE normalization.work_contribution_source_uuids (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            source_uuid uuid NOT NULL REFERENCES normalization.catalog_source_uuid_revisions(source_uuid),
            PRIMARY KEY(contribution_uuid,source_uuid)
        );

        CREATE FUNCTION normalization.bump_source_uuid_revisions(source_uuids uuid[]) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE source_key uuid;
        BEGIN
            FOR source_key IN SELECT DISTINCT key FROM unnest(source_uuids) key
                    WHERE key IS NOT NULL AND key<>'00000000-0000-0000-0000-000000000000'::uuid ORDER BY key LOOP
                INSERT INTO normalization.catalog_source_uuid_revisions(source_uuid,generation) VALUES(source_key,1)
                    ON CONFLICT(source_uuid) DO UPDATE SET generation=catalog_source_uuid_revisions.generation+1;
            END LOOP;
        END $$;

        CREATE FUNCTION normalization.retain_source_uuid_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Retain source UUID revisions; disable normalization workers for rollback';
        END $$;
        CREATE TRIGGER source_uuid_revision_retained BEFORE DELETE ON normalization.catalog_source_uuid_revisions
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_source_uuid_revision();
        CREATE TRIGGER source_uuid_revisions_retained BEFORE TRUNCATE ON normalization.catalog_source_uuid_revisions
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.retain_source_uuid_revision();

        -- This replaces migration 24's callback rather than adding a late callback after
        -- mapping/candidate locks. It retains that migration's catalog traversal and adds
        -- source UUIDs at the same boundary, after artist and source-identifier clocks.
        CREATE OR REPLACE FUNCTION normalization.record_catalog_revision() RETURNS trigger LANGUAGE plpgsql AS $$
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
                    source_uuids:=array_append(source_uuids,(row_value->>'uuid')::uuid);
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
                SELECT artist_ids||COALESCE(array_agg(artist_id),'{}'),
                    source_uuids||COALESCE(array_agg(uuid),'{}') INTO artist_ids,source_uuids
                    FROM sources WHERE id=ANY(source_ids);
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
            PERFORM normalization.bump_source_uuid_revisions(source_uuids);
            RETURN NULL;
        END $$;

        CREATE OR REPLACE FUNCTION normalization.fence_artist_catalog_delete() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- Prelock before FK cascades can delete mappings, take venue clocks or advance
            -- candidate pools. Child callbacks can safely revisit clocks already held here.
            PERFORM normalization.bump_catalog_revisions(ARRAY[OLD.uuid],
                ARRAY(SELECT upstream_identifier FROM sources WHERE artist_id=OLD.id));
            PERFORM normalization.bump_source_uuid_revisions(
                ARRAY(SELECT uuid FROM sources WHERE artist_id=OLD.id ORDER BY uuid));
            RETURN OLD;
        END $$;

        CREATE OR REPLACE FUNCTION normalization.record_catalog_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- TRUNCATE is offline maintenance. Its relation locks precede this callback;
            -- pause writers/publications and retain every tombstone when rows disappear.
            PERFORM normalization.bump_catalog_revisions(
                ARRAY(SELECT artist_uuid FROM normalization.catalog_artist_revisions),
                ARRAY(SELECT upstream_identifier FROM normalization.catalog_source_identity_revisions));
            PERFORM normalization.bump_source_uuid_revisions(
                ARRAY(SELECT source_uuid FROM normalization.catalog_source_uuid_revisions ORDER BY source_uuid));
            RETURN NULL;
        END $$;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization publication on rollback; retain source UUID clocks and contributor ownership."
        );
}
