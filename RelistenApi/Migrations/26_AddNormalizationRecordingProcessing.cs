using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains resumable recording extraction attempts without changing accepted domain data.</summary>
[Migration(26, "Retain recording normalization attempts and extraction proposals")]
public sealed class AddNormalizationRecordingProcessing : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.work_subjects DROP CONSTRAINT work_subjects_domain_check;
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_subjects_domain_check
            CHECK(domain IN ('venues','songs','compositions','people','extractions'));
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_extraction_kind
            CHECK(domain<>'extractions' OR subject_kind='recording');

        CREATE TABLE normalization.recording_processing_attempts (
            attempt_uuid uuid PRIMARY KEY,
            source_uuid uuid NOT NULL,
            processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            preparation_fetch_uuid uuid REFERENCES normalization.input_fetches(fetch_uuid),
            preparation jsonb,
            disposition text CHECK(disposition IN ('published','missing','unresolved','stale')),
            reason text CHECK(reason IS NULL OR length(trim(reason))>0),
            batch jsonb,
            extraction_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            completed_at timestamptz,
            CHECK((preparation IS NULL)=(preparation_fetch_uuid IS NULL)),
            CHECK(preparation IS NULL OR (jsonb_typeof(preparation)='object'
                AND preparation->>'FetchUuid' IS NOT NULL
                AND (preparation->>'FetchUuid')::uuid=preparation_fetch_uuid)),
            CHECK((disposition IS NULL)=(reason IS NULL)),
            CHECK((disposition IS NULL)=(completed_at IS NULL)),
            CHECK((extraction_work_uuid IS NOT NULL)=(batch IS NOT NULL)),
            CHECK(extraction_work_uuid IS NULL OR extraction_work_uuid=attempt_uuid),
            CHECK(batch IS NULL OR preparation IS NOT NULL),
            CHECK(disposition IS DISTINCT FROM 'published' OR batch IS NOT NULL)
        );

        CREATE FUNCTION normalization.retain_recording_processing_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.disposition IS NOT NULL THEN
                RAISE EXCEPTION 'Recording processing attempts and terminal outcomes are retained';
            END IF;
            IF (OLD.attempt_uuid,OLD.source_uuid,OLD.processing_version,OLD.created_at)
                    IS DISTINCT FROM (NEW.attempt_uuid,NEW.source_uuid,NEW.processing_version,NEW.created_at) THEN
                RAISE EXCEPTION 'Recording processing attempt requests are immutable';
            END IF;
            IF OLD.preparation IS NOT NULL AND (OLD.preparation,OLD.preparation_fetch_uuid)
                    IS DISTINCT FROM (NEW.preparation,NEW.preparation_fetch_uuid) THEN
                RAISE EXCEPTION 'Recording processing preparation is immutable';
            END IF;
            IF OLD.batch IS NOT NULL AND (OLD.batch,OLD.extraction_work_uuid)
                    IS DISTINCT FROM (NEW.batch,NEW.extraction_work_uuid) THEN
                RAISE EXCEPTION 'Completed recording extraction is immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_processing_attempt_retained BEFORE UPDATE OR DELETE
            ON normalization.recording_processing_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_processing_attempt();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop recording normalization processors on rollback; retain attempts and proposals."
        );
}
