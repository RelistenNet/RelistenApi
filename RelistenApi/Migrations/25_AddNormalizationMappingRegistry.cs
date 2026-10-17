using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// A provider-mapping lookup needs a fence even when no artist or recording exists yet.
/// The registry tracks mapping membership and provider qualification, not catalog row changes.
/// </summary>
[Migration(25, "Fence provider mapping lookups and contributed registry dependencies")]
public sealed class AddNormalizationMappingRegistry : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.catalog_mapping_registry_revision (
            singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
            generation bigint NOT NULL DEFAULT 0 CHECK(generation>=0)
        );
        INSERT INTO normalization.catalog_mapping_registry_revision(singleton) VALUES(true);
        CREATE TABLE normalization.work_mapping_registry_inputs (
            subject_uuid uuid PRIMARY KEY REFERENCES normalization.work_subjects(uuid),
            published_generation bigint
        );
        CREATE TABLE normalization.work_contribution_mapping_registry_inputs (
            contribution_uuid uuid PRIMARY KEY REFERENCES normalization.work_input_contributions(uuid)
        );

        CREATE FUNCTION normalization.retain_mapping_registry_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Retain the normalization mapping registry revision; disable normalization workers for rollback';
        END $$;
        CREATE TRIGGER mapping_registry_retain_row BEFORE DELETE ON normalization.catalog_mapping_registry_revision
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_mapping_registry_revision();
        CREATE TRIGGER mapping_registry_retain_table BEFORE TRUNCATE ON normalization.catalog_mapping_registry_revision
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.retain_mapping_registry_revision();

        CREATE FUNCTION normalization.record_mapping_registry_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF (to_jsonb(OLD)-ARRAY['created_at','updated_at'])
                        IS NOT DISTINCT FROM (to_jsonb(NEW)-ARRAY['created_at','updated_at']) THEN
                    RETURN NULL;
                END IF;
            END IF;
            UPDATE normalization.catalog_mapping_registry_revision SET generation=generation+1 WHERE singleton;
            RETURN NULL;
        END $$;

        -- PostgreSQL runs triggers for the same event in name order. These AFTER callbacks
        -- follow migration 24's normalization_catalog_* callbacks: artist clocks, source
        -- identity clocks, then the mapping registry. Publications use that same order.
        -- Do not acquire the registry in a BEFORE trigger. Catalog bulk maintenance spanning
        -- artists/providers must prelock all affected clocks consistently or retry the whole
        -- transaction on a deadlock; it must not retry just the failed statement.
        CREATE TRIGGER normalization_mapping_registry_revision AFTER INSERT OR UPDATE OR DELETE ON artists_upstream_sources
            FOR EACH ROW EXECUTE FUNCTION normalization.record_mapping_registry_revision();
        CREATE TRIGGER normalization_mapping_registry_revision AFTER INSERT OR UPDATE OR DELETE ON upstream_sources
            FOR EACH ROW EXECUTE FUNCTION normalization.record_mapping_registry_revision();
        -- TRUNCATE holds relation locks before callbacks and is an offline maintenance operation.
        CREATE TRIGGER normalization_mapping_registry_truncate AFTER TRUNCATE ON artists_upstream_sources
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_mapping_registry_revision();
        CREATE TRIGGER normalization_mapping_registry_truncate AFTER TRUNCATE ON upstream_sources
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_mapping_registry_revision();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization publication on rollback; retain mapping registry and contribution ownership."
        );
}
