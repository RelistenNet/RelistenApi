using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Imported venue facts need their own clock: an unused venue can have no artist.
/// Retained clocks also fence deletion and reuse of a stored catalog UUID.
/// </summary>
[Migration(29, "Fence imported venue facts independently of artist ownership")]
public sealed class AddNormalizationImportedVenueBindings : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.catalog_venue_revisions (
            venue_uuid uuid PRIMARY KEY,
            generation bigint NOT NULL DEFAULT 0 CHECK(generation>=0)
        );
        INSERT INTO normalization.catalog_venue_revisions(venue_uuid) SELECT uuid FROM venues;
        CREATE TABLE normalization.work_venue_inputs (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            venue_uuid uuid NOT NULL REFERENCES normalization.catalog_venue_revisions(venue_uuid),
            published_generation bigint,
            PRIMARY KEY(subject_uuid,venue_uuid)
        );
        CREATE TABLE normalization.work_contribution_venue_inputs (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            venue_uuid uuid NOT NULL REFERENCES normalization.catalog_venue_revisions(venue_uuid),
            PRIMARY KEY(contribution_uuid,venue_uuid)
        );

        CREATE FUNCTION normalization.bump_imported_venue_revisions(venue_keys uuid[]) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE venue_key uuid;
        BEGIN
            FOR venue_key IN SELECT DISTINCT key FROM unnest(venue_keys) key WHERE key IS NOT NULL ORDER BY key LOOP
                INSERT INTO normalization.catalog_venue_revisions(venue_uuid,generation) VALUES(venue_key,1)
                    ON CONFLICT(venue_uuid) DO UPDATE SET generation=catalog_venue_revisions.generation+1;
            END LOOP;
        END $$;

        CREATE FUNCTION normalization.retain_imported_venue_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Retain imported venue revisions; disable normalization workers for rollback';
        END $$;
        CREATE TRIGGER imported_venue_revision_retained BEFORE DELETE ON normalization.catalog_venue_revisions
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_imported_venue_revision();
        CREATE TRIGGER imported_venue_revisions_retained BEFORE TRUNCATE ON normalization.catalog_venue_revisions
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.retain_imported_venue_revision();

        CREATE FUNCTION normalization.record_imported_venue_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE venue_keys uuid[]:='{}';
        BEGIN
            IF TG_OP='UPDATE' AND (to_jsonb(OLD)-ARRAY['created_at','updated_at'])
                    IS NOT DISTINCT FROM (to_jsonb(NEW)-ARRAY['created_at','updated_at']) THEN
                RETURN NULL;
            END IF;
            IF TG_OP<>'INSERT' THEN venue_keys:=array_append(venue_keys,OLD.uuid); END IF;
            IF TG_OP<>'DELETE' THEN venue_keys:=array_append(venue_keys,NEW.uuid); END IF;
            PERFORM normalization.bump_imported_venue_revisions(venue_keys);
            RETURN NULL;
        END $$;

        -- Same-event callbacks run in name order. Migration 24's catalog callback takes
        -- artist and source-identity clocks before this imported-venue callback.
        CREATE TRIGGER normalization_imported_venue_revision AFTER INSERT OR UPDATE OR DELETE ON venues
            FOR EACH ROW EXECUTE FUNCTION normalization.record_imported_venue_revision();

        CREATE FUNCTION normalization.fence_artist_imported_venue_delete() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- The preceding normalization_catalog_artist_delete BEFORE callback takes
            -- artist/source clocks. Prelock owned venue clocks before FK cascades can
            -- acquire mapping-registry locks by deleting this artist's provider mappings.
            PERFORM normalization.bump_imported_venue_revisions(
                ARRAY(SELECT uuid FROM venues WHERE artist_id=OLD.id ORDER BY uuid));
            RETURN OLD;
        END $$;
        CREATE TRIGGER normalization_imported_venue_artist_delete BEFORE DELETE ON artists
            FOR EACH ROW EXECUTE FUNCTION normalization.fence_artist_imported_venue_delete();

        CREATE FUNCTION normalization.record_imported_venue_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM normalization.bump_imported_venue_revisions(
                ARRAY(SELECT venue_uuid FROM normalization.catalog_venue_revisions ORDER BY venue_uuid));
            RETURN NULL;
        END $$;
        -- TRUNCATE is offline maintenance: relation locks precede callbacks. Pause writers
        -- and publications first. Clocks survive even when CASCADE removes every public row.
        CREATE TRIGGER normalization_imported_venue_truncate AFTER TRUNCATE ON venues
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_imported_venue_truncate();
        CREATE TRIGGER normalization_imported_venue_truncate AFTER TRUNCATE ON artists
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_imported_venue_truncate();

        -- Publication order is artists -> source identifiers -> imported venues -> mapping
        -- registry -> retained resources -> work owners. Cross-owner catalog bulk writes
        -- must prelock all affected clocks in that order, or retry the entire transaction
        -- on a deadlock. Retrying an individual failed SQL statement is not sufficient.
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization publication on rollback; retain imported venue clocks and contribution ownership."
        );
}
