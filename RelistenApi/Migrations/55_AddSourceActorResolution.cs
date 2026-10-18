using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Source actor proposals retain their own history without changing identities, aliases or credits.</summary>
[Migration(55, "Retain source actor resolution proposals and recovery checkpoints")]
public sealed class AddSourceActorResolution : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.source_actor_resolution_attempts (
            attempt_uuid uuid PRIMARY KEY,
            source_uuid uuid NOT NULL,
            extraction_attempt_uuid uuid NOT NULL REFERENCES normalization.recording_processing_attempts(attempt_uuid),
            processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            preparation jsonb,
            batch jsonb,
            work_uuid uuid REFERENCES normalization.work_runs(uuid),
            disposition text CHECK(disposition IN ('published','unresolved','stale')),
            reason text CHECK(reason IS NULL OR length(trim(reason))>0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),completed_at timestamptz,
            CHECK((disposition IS NULL)=(reason IS NULL)),
            CHECK((disposition IS NULL)=(completed_at IS NULL)),
            CHECK((batch IS NULL)=(work_uuid IS NULL)),
            CHECK(work_uuid IS NULL OR work_uuid=attempt_uuid),
            CHECK(batch IS NULL OR preparation IS NOT NULL),
            CHECK(disposition IS DISTINCT FROM 'published' OR batch IS NOT NULL)
        );
        CREATE INDEX source_actor_resolution_source ON normalization.source_actor_resolution_attempts(source_uuid,created_at DESC,attempt_uuid DESC);
        CREATE FUNCTION normalization.retain_source_actor_resolution_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.disposition IS NOT NULL THEN
                RAISE EXCEPTION 'Source actor resolution attempts and terminal outcomes are retained';
            END IF;
            IF (OLD.attempt_uuid,OLD.source_uuid,OLD.extraction_attempt_uuid,OLD.processing_version,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.source_uuid,NEW.extraction_attempt_uuid,NEW.processing_version,NEW.created_at) THEN
                RAISE EXCEPTION 'Source actor resolution requests are immutable';
            END IF;
            IF OLD.preparation IS NOT NULL AND OLD.preparation IS DISTINCT FROM NEW.preparation THEN
                RAISE EXCEPTION 'Source actor resolution preparation is immutable';
            END IF;
            IF OLD.batch IS NOT NULL AND (OLD.batch,OLD.work_uuid) IS DISTINCT FROM (NEW.batch,NEW.work_uuid) THEN
                RAISE EXCEPTION 'Source actor resolution checkpoints are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER source_actor_resolution_attempt_retained BEFORE UPDATE OR DELETE
            ON normalization.source_actor_resolution_attempts FOR EACH ROW EXECUTE FUNCTION normalization.retain_source_actor_resolution_attempt();
        CREATE TRIGGER source_actor_resolution_attempts_retained BEFORE TRUNCATE
            ON normalization.source_actor_resolution_attempts FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop source actor resolution on rollback; retain proposal attempts and their evidence for recovery."
        );
}
