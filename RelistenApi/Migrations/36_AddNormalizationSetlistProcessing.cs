using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains setlist proposal attempts independently of accepted editions and catalog lifetime.</summary>
[Migration(36, "Retain normalization setlist processing attempts and proposal checkpoints")]
public sealed class AddNormalizationSetlistProcessing : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.work_subjects DROP CONSTRAINT work_extraction_kind;
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_extraction_kind
            CHECK(domain<>'extractions' OR subject_kind IN ('recording','setlist'));

        CREATE TABLE normalization.setlist_processing_registrations (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            provider text NOT NULL,resource_type text NOT NULL,provider_key text NOT NULL,
            expected_artist_uuid uuid,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE NULLS NOT DISTINCT(provider,resource_type,provider_key,expected_artist_uuid)
        );
        CREATE TABLE normalization.setlist_processing_attempts (
            attempt_uuid uuid PRIMARY KEY,
            registration_uuid uuid NOT NULL REFERENCES normalization.setlist_processing_registrations(uuid),
            processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            request jsonb NOT NULL,
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            preparation jsonb,
            batch jsonb,
            extraction_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            disposition text CHECK(disposition IN ('published','unresolved','stale')),
            reason text CHECK(reason IS NULL OR length(trim(reason))>0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),completed_at timestamptz,
            CHECK((disposition IS NULL)=(reason IS NULL)),
            CHECK((disposition IS NULL)=(completed_at IS NULL)),
            CHECK((batch IS NULL)=(extraction_work_uuid IS NULL)),
            CHECK(extraction_work_uuid IS NULL OR extraction_work_uuid=attempt_uuid),
            CHECK(batch IS NULL OR preparation IS NOT NULL),
            CHECK(disposition IS DISTINCT FROM 'published' OR batch IS NOT NULL)
        );
        CREATE INDEX setlist_processing_latest_attempt
            ON normalization.setlist_processing_attempts(registration_uuid,created_at DESC,attempt_uuid DESC);

        CREATE FUNCTION normalization.retain_setlist_processing_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.disposition IS NOT NULL THEN
                RAISE EXCEPTION 'Setlist processing attempts and terminal outcomes are retained';
            END IF;
            IF (OLD.attempt_uuid,OLD.registration_uuid,OLD.processing_version,OLD.request,OLD.input_revision_uuid,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.registration_uuid,NEW.processing_version,NEW.request,NEW.input_revision_uuid,NEW.created_at) THEN
                RAISE EXCEPTION 'Setlist processing attempt requests are immutable';
            END IF;
            IF OLD.preparation IS NOT NULL AND OLD.preparation IS DISTINCT FROM NEW.preparation THEN
                RAISE EXCEPTION 'Setlist processing preparation is immutable';
            END IF;
            IF OLD.batch IS NOT NULL AND (OLD.batch,OLD.extraction_work_uuid)
                IS DISTINCT FROM (NEW.batch,NEW.extraction_work_uuid) THEN
                RAISE EXCEPTION 'Setlist proposal checkpoints are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER setlist_processing_attempt_retained BEFORE UPDATE OR DELETE
            ON normalization.setlist_processing_attempts FOR EACH ROW
            EXECUTE FUNCTION normalization.retain_setlist_processing_attempt();
        CREATE TRIGGER setlist_processing_registration_retained BEFORE UPDATE OR DELETE
            ON normalization.setlist_processing_registrations FOR EACH ROW
            EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_processing_attempts_retained BEFORE TRUNCATE
            ON normalization.setlist_processing_attempts FOR EACH STATEMENT
            EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_processing_registrations_retained BEFORE TRUNCATE
            ON normalization.setlist_processing_registrations FOR EACH STATEMENT
            EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop setlist processors before rollback; retain their attempts and proposal checkpoints."
        );
}
