using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains exact upstream inputs independently of catalog row lifetime and extraction versions.</summary>
[Migration(18, "Retain normalization inputs and ordered fetch receipts")]
public sealed class AddNormalizationEvidence : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.input_resources (
            uuid uuid PRIMARY KEY,
            provider text NOT NULL CHECK (length(trim(provider))>0),
            resource_type text NOT NULL CHECK (length(trim(resource_type))>0),
            provider_key text NOT NULL CHECK (length(trim(provider_key))>0),
            generation bigint NOT NULL DEFAULT 0 CHECK (generation>=0),
            current_revision_uuid uuid,
            UNIQUE(provider,resource_type,provider_key),
            CHECK ((generation=0) = (current_revision_uuid IS NULL))
        );

        CREATE TABLE normalization.input_revisions (
            uuid uuid PRIMARY KEY,
            resource_uuid uuid NOT NULL REFERENCES normalization.input_resources(uuid),
            payload bytea NOT NULL,
            sha256 text NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
            media_type text NOT NULL CHECK (length(trim(media_type))>0),
            capture_version text NOT NULL CHECK (length(trim(capture_version))>0),
            captured_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(resource_uuid,sha256,media_type,capture_version),
            UNIQUE(resource_uuid,uuid)
        );
        ALTER TABLE normalization.input_resources ADD CONSTRAINT input_current_revision_resource
            FOREIGN KEY(uuid,current_revision_uuid) REFERENCES normalization.input_revisions(resource_uuid,uuid);

        CREATE TABLE normalization.input_fetches (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            fetch_uuid uuid NOT NULL UNIQUE,
            resource_uuid uuid NOT NULL REFERENCES normalization.input_resources(uuid),
            input_revision_uuid uuid NOT NULL,
            expected_generation bigint NOT NULL CHECK (expected_generation>=0),
            resource_generation bigint NOT NULL CHECK (resource_generation>=0),
            accepted_as_current boolean NOT NULL,
            advanced_current boolean NOT NULL,
            observed_at timestamptz NOT NULL,
            provider_revision text,
            captured_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            FOREIGN KEY(resource_uuid,input_revision_uuid) REFERENCES normalization.input_revisions(resource_uuid,uuid),
            CHECK (NOT advanced_current OR accepted_as_current)
        );
        CREATE INDEX input_fetches_resource_sequence ON normalization.input_fetches(resource_uuid,sequence);

        CREATE FUNCTION normalization.reject_input_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Normalization input history is immutable';
        END
        $$;
        CREATE TRIGGER input_revisions_immutable BEFORE UPDATE OR DELETE ON normalization.input_revisions
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER input_fetches_immutable BEFORE UPDATE OR DELETE ON normalization.input_fetches
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retained evidence survives rollback. Stop normalization writers instead."
        );
}
