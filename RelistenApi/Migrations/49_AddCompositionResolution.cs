using System;
using SimpleMigrations;

namespace Migrations;

[Migration(49, "Retain composition resolution evidence and authenticated publication stages")]
public sealed class AddCompositionResolution : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.composition_acceptance_policies(
            uuid uuid PRIMARY KEY,artifact_hash text NOT NULL,artifact jsonb NOT NULL,
            evaluation_uuid uuid NOT NULL UNIQUE,evaluation_hash text NOT NULL,evaluation jsonb NOT NULL,
            registered_by text NOT NULL,registered_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE normalization.composition_acceptance_active(
            policy_key_uuid uuid PRIMARY KEY,artifact_uuid uuid REFERENCES normalization.composition_acceptance_policies(uuid)
        );
        CREATE TRIGGER composition_acceptance_policies_immutable BEFORE UPDATE OR DELETE
            ON normalization.composition_acceptance_policies FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER composition_acceptance_policies_retained BEFORE TRUNCATE
            ON normalization.composition_acceptance_policies FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.composition_resolution_attempts(
            attempt_uuid uuid PRIMARY KEY,claim jsonb NOT NULL,processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            previous_attempt_uuid uuid REFERENCES normalization.composition_resolution_attempts(attempt_uuid),
            preparation jsonb,proposal jsonb,result jsonb,created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(previous_attempt_uuid IS NULL OR previous_attempt_uuid<>attempt_uuid),
            CHECK(proposal IS NULL OR preparation IS NOT NULL)
        );
        CREATE INDEX composition_resolution_claim ON normalization.composition_resolution_attempts((claim->>'RepertoireSongUuid'),created_at DESC);
        CREATE TABLE normalization.composition_resolution_checkpoints(
            attempt_uuid uuid NOT NULL REFERENCES normalization.composition_resolution_attempts(attempt_uuid),
            ordinal bigint NOT NULL CHECK(ordinal>=0),checkpoint jsonb NOT NULL,
            PRIMARY KEY(attempt_uuid,ordinal)
        );
        CREATE TABLE normalization.composition_resolution_stages(
            work_uuid uuid PRIMARY KEY,attempt_uuid uuid NOT NULL REFERENCES normalization.composition_resolution_attempts(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0),stage jsonb NOT NULL,ticket jsonb,receipt jsonb,
            admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),UNIQUE(attempt_uuid,ordinal),
            CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=work_uuid),
            CHECK(receipt IS NULL OR ticket IS NOT NULL)
        );
        CREATE FUNCTION normalization.retain_composition_resolution_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.result IS NOT NULL THEN RAISE EXCEPTION 'Composition resolution history is retained'; END IF;
            IF (OLD.attempt_uuid,OLD.claim,OLD.processing_version,OLD.previous_attempt_uuid,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.claim,NEW.processing_version,NEW.previous_attempt_uuid,NEW.created_at)
                OR (OLD.preparation IS NOT NULL AND OLD.preparation IS DISTINCT FROM NEW.preparation)
                OR (OLD.proposal IS NOT NULL AND OLD.proposal IS DISTINCT FROM NEW.proposal) THEN
                RAISE EXCEPTION 'Composition resolution request and original evidence are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER composition_resolution_attempt_retained BEFORE UPDATE OR DELETE ON normalization.composition_resolution_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_composition_resolution_attempt();
        CREATE FUNCTION normalization.retain_composition_resolution_stage() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.work_uuid,OLD.attempt_uuid,OLD.ordinal,OLD.stage)
                IS DISTINCT FROM (NEW.work_uuid,NEW.attempt_uuid,NEW.ordinal,NEW.stage)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND OLD.receipt IS DISTINCT FROM NEW.receipt) THEN
                RAISE EXCEPTION 'Composition resolution stages and publication receipts are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER composition_resolution_stage_retained BEFORE UPDATE OR DELETE ON normalization.composition_resolution_stages
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_composition_resolution_stage();
        CREATE TRIGGER composition_resolution_checkpoints_immutable BEFORE UPDATE OR DELETE ON normalization.composition_resolution_checkpoints
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER composition_resolution_attempts_retained BEFORE TRUNCATE ON normalization.composition_resolution_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER composition_resolution_checkpoints_retained BEFORE TRUNCATE ON normalization.composition_resolution_checkpoints
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER composition_resolution_stages_retained BEFORE TRUNCATE ON normalization.composition_resolution_stages
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Revoke automatic composition acceptance and retain its evidence and publication receipts."
        );
}
