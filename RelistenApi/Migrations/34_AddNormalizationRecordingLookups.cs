using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Lookup owners can settle absent catalog associations without replacing a successful
/// extraction. Bound outcomes create durable downstream delivery in the same publication.
/// </summary>
[Migration(34, "Retain recording lookup attempts and processing handoffs")]
public sealed class AddNormalizationRecordingLookups : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.work_subjects DROP CONSTRAINT work_subjects_domain_check;
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_subjects_domain_check
            CHECK(domain IN ('venues','songs','compositions','people','extractions','lookups'));
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_lookup_kind
            CHECK(domain<>'lookups' OR subject_kind='recording');

        CREATE TABLE normalization.recording_lookups (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            mode text NOT NULL CHECK(mode IN ('LegacyCatalog','RetainedProvider')),
            provider text NOT NULL CHECK(length(trim(provider))>0),
            resource_type text NOT NULL CHECK(length(trim(resource_type))>0),
            provider_key text NOT NULL CHECK(length(trim(provider_key))>0),
            expected_artist_uuid uuid,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE NULLS NOT DISTINCT(mode,provider,resource_type,provider_key,expected_artist_uuid)
        );
        CREATE TABLE normalization.recording_lookup_attempts (
            attempt_uuid uuid PRIMARY KEY,
            lookup_uuid uuid NOT NULL REFERENCES normalization.recording_lookups(uuid),
            processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            request jsonb NOT NULL CHECK(jsonb_typeof(request)='object'),
            input_revision_uuid uuid REFERENCES normalization.input_revisions(uuid),
            preparation jsonb,
            preparation_fetch_uuid uuid REFERENCES normalization.input_fetches(fetch_uuid),
            result jsonb,
            disposition text CHECK(disposition IN ('Published','Stale')),
            publication_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            completed_at timestamptz,
            CHECK((result IS NULL)=(disposition IS NULL)),
            CHECK((completed_at IS NULL)=(disposition IS NULL)),
            CHECK((request#>>'{Input,InputRevisionUuid}')::uuid IS NOT DISTINCT FROM input_revision_uuid),
            CHECK(preparation IS NOT NULL OR preparation_fetch_uuid IS NULL),
            CHECK(preparation IS NULL OR (preparation->>'FetchUuid')::uuid IS NOT DISTINCT FROM preparation_fetch_uuid),
            CHECK(preparation IS NULL OR (jsonb_typeof(preparation)='object' AND
                COALESCE(jsonb_typeof(preparation->'Binding')='object',false)
                    <>COALESCE(jsonb_typeof(preparation->'AbsenceProbe')='object',false))),
            CHECK((disposition IS NOT DISTINCT FROM 'Published')=(publication_work_uuid IS NOT NULL)),
            CHECK(publication_work_uuid IS NULL OR publication_work_uuid=attempt_uuid),
            CHECK(disposition IS DISTINCT FROM 'Published' OR preparation IS NOT NULL)
        );
        CREATE TABLE normalization.recording_lookup_handoffs (
            uuid uuid PRIMARY KEY,
            lookup_attempt_uuid uuid NOT NULL UNIQUE REFERENCES normalization.recording_lookup_attempts(attempt_uuid),
            processing_attempt_uuid uuid NOT NULL UNIQUE,
            completed_result jsonb,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            completed_at timestamptz,
            CHECK((completed_result IS NULL)=(completed_at IS NULL))
        );
        CREATE INDEX recording_lookup_pending_handoffs ON normalization.recording_lookup_handoffs(uuid)
            WHERE completed_result IS NULL;

        CREATE FUNCTION normalization.retain_recording_lookup() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Recording lookup registrations are immutable';
        END $$;
        CREATE TRIGGER recording_lookup_retained BEFORE UPDATE OR DELETE ON normalization.recording_lookups
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_lookup();

        CREATE FUNCTION normalization.retain_recording_lookup_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.result IS NOT NULL THEN
                RAISE EXCEPTION 'Recording lookup attempts and terminal outcomes are retained';
            END IF;
            IF (OLD.attempt_uuid,OLD.lookup_uuid,OLD.processing_version,OLD.request,OLD.input_revision_uuid,OLD.created_at)
                    IS DISTINCT FROM (NEW.attempt_uuid,NEW.lookup_uuid,NEW.processing_version,NEW.request,NEW.input_revision_uuid,NEW.created_at) THEN
                RAISE EXCEPTION 'Recording lookup request is immutable';
            END IF;
            IF OLD.preparation IS NOT NULL AND (OLD.preparation,OLD.preparation_fetch_uuid)
                    IS DISTINCT FROM (NEW.preparation,NEW.preparation_fetch_uuid) THEN
                RAISE EXCEPTION 'Recording lookup preparation is immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_lookup_attempt_retained BEFORE UPDATE OR DELETE ON normalization.recording_lookup_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_lookup_attempt();

        CREATE FUNCTION normalization.retain_recording_lookup_handoff() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.completed_result IS NOT NULL THEN
                RAISE EXCEPTION 'Recording lookup handoffs and acknowledgements are retained';
            END IF;
            IF (OLD.uuid,OLD.lookup_attempt_uuid,OLD.processing_attempt_uuid,OLD.created_at)
                    IS DISTINCT FROM (NEW.uuid,NEW.lookup_attempt_uuid,NEW.processing_attempt_uuid,NEW.created_at) THEN
                RAISE EXCEPTION 'Recording lookup delivery is immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_lookup_handoff_retained BEFORE UPDATE OR DELETE ON normalization.recording_lookup_handoffs
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_lookup_handoff();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop lookup processors and delivery workers on rollback; retain lookup history and handoffs."
        );
}
