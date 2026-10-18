using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Inactive policy artifacts and recoverable accepted-data stages reuse the existing Work ledger.</summary>
[Migration(46, "Add evaluated people policy artifacts and accepted resolution stages")]
public sealed class AddNormalizationPeopleAcceptance : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.people_acceptance_policies(
            uuid uuid PRIMARY KEY,artifact_hash text NOT NULL,artifact jsonb NOT NULL,
            evaluation_uuid uuid NOT NULL UNIQUE,evaluation_hash text NOT NULL,evaluation jsonb NOT NULL,
            registered_by text NOT NULL,registered_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE normalization.people_acceptance_active(
            policy_key_uuid uuid PRIMARY KEY,artifact_uuid uuid REFERENCES normalization.people_acceptance_policies(uuid)
        );
        CREATE TRIGGER people_acceptance_policies_immutable BEFORE UPDATE OR DELETE
            ON normalization.people_acceptance_policies FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER people_acceptance_policies_retained BEFORE TRUNCATE
            ON normalization.people_acceptance_policies FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.people_acceptance_applications(
            attempt_uuid uuid PRIMARY KEY,resolution_attempt_uuid uuid NOT NULL REFERENCES normalization.people_resolution_attempts(attempt_uuid),
            source_uuid uuid NOT NULL,version text NOT NULL,plan jsonb,result jsonb,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE normalization.people_acceptance_stages(
            work_uuid uuid PRIMARY KEY,application_uuid uuid NOT NULL REFERENCES normalization.people_acceptance_applications(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0),stage jsonb NOT NULL,ticket jsonb,receipt jsonb,
            admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            UNIQUE(application_uuid,ordinal),CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),
            CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=work_uuid),CHECK(receipt IS NULL OR ticket IS NOT NULL)
        );
        CREATE FUNCTION normalization.retain_people_acceptance_application() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.result IS NOT NULL THEN RAISE EXCEPTION 'People acceptance history is retained'; END IF;
            IF (OLD.attempt_uuid,OLD.resolution_attempt_uuid,OLD.source_uuid,OLD.version,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.resolution_attempt_uuid,NEW.source_uuid,NEW.version,NEW.created_at)
                OR (OLD.plan IS NOT NULL AND OLD.plan IS DISTINCT FROM NEW.plan) THEN
                RAISE EXCEPTION 'People acceptance request and original plan are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER people_acceptance_application_retained BEFORE UPDATE OR DELETE ON normalization.people_acceptance_applications
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_people_acceptance_application();
        CREATE FUNCTION normalization.retain_people_acceptance_stage() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.work_uuid,OLD.application_uuid,OLD.ordinal,OLD.stage)
                IS DISTINCT FROM (NEW.work_uuid,NEW.application_uuid,NEW.ordinal,NEW.stage)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND OLD.receipt IS DISTINCT FROM NEW.receipt) THEN
                RAISE EXCEPTION 'People acceptance stage and publication receipts are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER people_acceptance_stage_retained BEFORE UPDATE OR DELETE ON normalization.people_acceptance_stages
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_people_acceptance_stage();
        CREATE TRIGGER people_acceptance_applications_retained BEFORE TRUNCATE ON normalization.people_acceptance_applications
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER people_acceptance_stages_retained BEFORE TRUNCATE ON normalization.people_acceptance_stages
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Revoke automatic acceptance and retain its evidence and publication receipts."
        );
}
