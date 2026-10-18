using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retained source publication checkpoints supplement 53 authority without rewriting its history.</summary>
[Migration(57, "Retain source actor alias applications and complete original-person maps")]
public sealed class AddSourceActorAliasPublication : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.source_actor_alias_applications (
            attempt_uuid uuid PRIMARY KEY,
            resolution_attempt_uuid uuid NOT NULL REFERENCES normalization.source_actor_resolution_attempts(attempt_uuid),
            source_uuid uuid NOT NULL,
            version text NOT NULL CHECK(length(trim(version))>0),
            plan jsonb,result jsonb,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(result IS NULL OR plan IS NOT NULL)
        );
        CREATE TABLE normalization.source_actor_alias_person_stages (
            stage_uuid uuid PRIMARY KEY,
            work_uuid uuid NOT NULL UNIQUE,
            application_uuid uuid NOT NULL REFERENCES normalization.source_actor_alias_applications(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0),
            contribution_revision bigint NOT NULL CHECK(contribution_revision>0),
            person_uuid uuid NOT NULL REFERENCES normalization.people(uuid),
            owner_subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            predecessor_stage_uuid uuid REFERENCES normalization.source_actor_alias_person_stages(stage_uuid),
            configured_predecessor_stage_uuid uuid REFERENCES normalization.source_actor_alias_person_stages(stage_uuid),
            extraction_attempt_uuid uuid NOT NULL REFERENCES normalization.recording_processing_attempts(attempt_uuid),
            stage jsonb NOT NULL,stage_header jsonb NOT NULL,ticket jsonb,receipt jsonb,receipt_header jsonb,terminal_reason text,
            admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            UNIQUE(application_uuid,ordinal),
            CHECK(stage_uuid IS DISTINCT FROM predecessor_stage_uuid),
            CHECK(stage_uuid IS DISTINCT FROM configured_predecessor_stage_uuid),
            CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),
            CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=work_uuid),
            CHECK(receipt IS NULL OR ticket IS NOT NULL),
            CHECK((receipt IS NULL)=(receipt_header IS NULL)),
            CHECK(terminal_reason IS NULL OR (receipt IS NULL AND length(trim(terminal_reason))>0))
        );
        CREATE INDEX source_actor_alias_application_source ON normalization.source_actor_alias_applications(source_uuid,attempt_uuid);
        CREATE INDEX source_actor_alias_stage_source_people ON normalization.source_actor_alias_person_stages(application_uuid,person_uuid)
            WHERE receipt IS NOT NULL;
        CREATE INDEX source_actor_alias_stage_configuration ON normalization.source_actor_alias_person_stages(person_uuid,contribution_revision);
        CREATE INDEX source_actor_alias_stage_owner ON normalization.source_actor_alias_person_stages(owner_subject_uuid,admitted_work_uuid);
        CREATE FUNCTION normalization.retain_source_actor_alias_application() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.result IS NOT NULL THEN
                RAISE EXCEPTION 'Source actor alias application history is retained';
            END IF;
            IF (OLD.attempt_uuid,OLD.resolution_attempt_uuid,OLD.source_uuid,OLD.version,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.resolution_attempt_uuid,NEW.source_uuid,NEW.version,NEW.created_at)
                OR (OLD.plan IS NOT NULL AND OLD.plan IS DISTINCT FROM NEW.plan) THEN
                RAISE EXCEPTION 'Source actor alias request and first plan are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER source_actor_alias_application_retained BEFORE UPDATE OR DELETE
            ON normalization.source_actor_alias_applications FOR EACH ROW EXECUTE FUNCTION normalization.retain_source_actor_alias_application();
        CREATE FUNCTION normalization.retain_source_actor_alias_stage() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.stage_uuid,OLD.work_uuid,OLD.application_uuid,OLD.ordinal,OLD.person_uuid,
                OLD.owner_subject_uuid,OLD.contribution_revision,OLD.predecessor_stage_uuid,OLD.configured_predecessor_stage_uuid,OLD.extraction_attempt_uuid,OLD.stage,OLD.stage_header)
                IS DISTINCT FROM (NEW.stage_uuid,NEW.work_uuid,NEW.application_uuid,NEW.ordinal,NEW.person_uuid,
                NEW.owner_subject_uuid,NEW.contribution_revision,NEW.predecessor_stage_uuid,NEW.configured_predecessor_stage_uuid,NEW.extraction_attempt_uuid,NEW.stage,NEW.stage_header)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND (OLD.receipt,OLD.receipt_header) IS DISTINCT FROM (NEW.receipt,NEW.receipt_header))
                OR (OLD.terminal_reason IS NOT NULL AND OLD.terminal_reason IS DISTINCT FROM NEW.terminal_reason)
                OR (OLD.receipt IS NOT NULL AND NEW.terminal_reason IS NOT NULL) THEN
                RAISE EXCEPTION 'Source actor alias preparation and append-once outcomes are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER source_actor_alias_stage_retained BEFORE UPDATE OR DELETE
            ON normalization.source_actor_alias_person_stages FOR EACH ROW EXECUTE FUNCTION normalization.retain_source_actor_alias_stage();
        CREATE TRIGGER source_actor_alias_applications_retained BEFORE TRUNCATE
            ON normalization.source_actor_alias_applications FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER source_actor_alias_stages_retained BEFORE TRUNCATE
            ON normalization.source_actor_alias_person_stages FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop new source alias publication; retain accepted authority, complete clears and all application history."
        );
}
