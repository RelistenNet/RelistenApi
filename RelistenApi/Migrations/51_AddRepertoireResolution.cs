using System;
using SimpleMigrations;

namespace Migrations;

[Migration(51, "Retain repertoire resolution registrations and publication stages")]
public sealed class AddRepertoireResolution : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.repertoire_acceptance_policies(
            uuid uuid PRIMARY KEY,artifact_hash text NOT NULL,artifact jsonb NOT NULL,
            evaluation_uuid uuid NOT NULL UNIQUE,evaluation_hash text NOT NULL,evaluation jsonb NOT NULL,
            registered_by text NOT NULL,registered_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE normalization.repertoire_acceptance_active(
            policy_key_uuid uuid PRIMARY KEY,artifact_uuid uuid REFERENCES normalization.repertoire_acceptance_policies(uuid)
        );
        CREATE TRIGGER repertoire_acceptance_policies_immutable BEFORE UPDATE OR DELETE ON normalization.repertoire_acceptance_policies
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER repertoire_acceptance_policies_retained BEFORE TRUNCATE ON normalization.repertoire_acceptance_policies
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.repertoire_resolution_registrations(
            registration_uuid uuid PRIMARY KEY,provider text NOT NULL,resource_type text NOT NULL,provider_key text NOT NULL,
            expected_artist_uuid uuid,current_attempt_uuid uuid,
            UNIQUE NULLS NOT DISTINCT(provider,resource_type,provider_key,expected_artist_uuid)
        );
        CREATE TABLE normalization.repertoire_resolution_attempts(
            attempt_uuid uuid PRIMARY KEY,sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,registration_uuid uuid NOT NULL REFERENCES normalization.repertoire_resolution_registrations(registration_uuid),
            registration jsonb NOT NULL,request jsonb NOT NULL,processing_version text NOT NULL,
            previous_attempt_uuid uuid REFERENCES normalization.repertoire_resolution_attempts(attempt_uuid),preparation jsonb,result jsonb,checkpoint_revision bigint NOT NULL DEFAULT 0 CHECK(checkpoint_revision>=0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),CHECK(previous_attempt_uuid IS NULL OR previous_attempt_uuid<>attempt_uuid)
        );
        ALTER TABLE normalization.repertoire_resolution_registrations ADD FOREIGN KEY(current_attempt_uuid)
            REFERENCES normalization.repertoire_resolution_attempts(attempt_uuid);
        CREATE INDEX repertoire_resolution_registration_attempts ON normalization.repertoire_resolution_attempts(registration_uuid,sequence DESC);
        CREATE INDEX repertoire_resolution_retries ON normalization.repertoire_resolution_attempts(previous_attempt_uuid);
        CREATE TABLE normalization.repertoire_resolution_stages(
            stage_uuid uuid PRIMARY KEY,work_uuid uuid NOT NULL UNIQUE,
            attempt_uuid uuid NOT NULL REFERENCES normalization.repertoire_resolution_attempts(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0 AND ordinal<2000),stage jsonb NOT NULL,ticket jsonb,receipt jsonb,superseded boolean NOT NULL DEFAULT false,failure_reason text,own_effects jsonb,
            admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),UNIQUE(attempt_uuid,ordinal),
            CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=work_uuid),
            CHECK(receipt IS NULL OR ticket IS NOT NULL),CHECK(receipt IS NULL OR failure_reason IS NULL),CHECK(own_effects IS NULL OR receipt IS NOT NULL)
        );
        CREATE TABLE normalization.repertoire_resolution_associations(
            decision_uuid uuid PRIMARY KEY,sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,created_at timestamptz NOT NULL DEFAULT clock_timestamp(),registration_uuid uuid NOT NULL REFERENCES normalization.repertoire_resolution_registrations(registration_uuid),
            attempt_uuid uuid NOT NULL REFERENCES normalization.repertoire_resolution_attempts(attempt_uuid),
            claim_uuid uuid NOT NULL,correspondence_key text NOT NULL,repertoire_song_uuid uuid,
            disposition text NOT NULL CHECK(disposition IN ('Bound','Provisional','Review','Unresolved')),
            reason text NOT NULL,authority text NOT NULL,evidence jsonb NOT NULL,
            UNIQUE(attempt_uuid,claim_uuid),UNIQUE(attempt_uuid,correspondence_key)
        );
        CREATE INDEX repertoire_resolution_association_current ON normalization.repertoire_resolution_associations(registration_uuid,correspondence_key,attempt_uuid);
        CREATE FUNCTION normalization.retain_repertoire_resolution_registration() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.registration_uuid,OLD.provider,OLD.resource_type,OLD.provider_key,OLD.expected_artist_uuid)
                IS DISTINCT FROM (NEW.registration_uuid,NEW.provider,NEW.resource_type,NEW.provider_key,NEW.expected_artist_uuid) THEN
                RAISE EXCEPTION 'Repertoire source lineage is retained';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER repertoire_resolution_registration_retained BEFORE UPDATE OR DELETE ON normalization.repertoire_resolution_registrations
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_repertoire_resolution_registration();
        CREATE FUNCTION normalization.retain_repertoire_resolution_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.result IS NOT NULL OR (OLD.attempt_uuid,OLD.sequence,OLD.registration_uuid,OLD.registration,OLD.request,OLD.processing_version,OLD.previous_attempt_uuid,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.sequence,NEW.registration_uuid,NEW.registration,NEW.request,NEW.processing_version,NEW.previous_attempt_uuid,NEW.created_at)
                OR (OLD.preparation IS NOT NULL AND OLD.preparation IS DISTINCT FROM NEW.preparation)
                OR NEW.checkpoint_revision<>OLD.checkpoint_revision+1 THEN
                RAISE EXCEPTION 'Repertoire requests, original preparations and terminal receipts are retained';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER repertoire_resolution_attempt_retained BEFORE UPDATE OR DELETE ON normalization.repertoire_resolution_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_repertoire_resolution_attempt();
        CREATE FUNCTION normalization.retain_repertoire_resolution_stage() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.stage_uuid,OLD.work_uuid,OLD.attempt_uuid,OLD.ordinal,OLD.stage)
                IS DISTINCT FROM (NEW.stage_uuid,NEW.work_uuid,NEW.attempt_uuid,NEW.ordinal,NEW.stage)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND OLD.receipt IS DISTINCT FROM NEW.receipt)
                OR (OLD.superseded AND NOT NEW.superseded)
                OR (OLD.failure_reason IS NOT NULL AND OLD.failure_reason IS DISTINCT FROM NEW.failure_reason)
                OR (OLD.own_effects IS NOT NULL AND OLD.own_effects IS DISTINCT FROM NEW.own_effects) THEN
                RAISE EXCEPTION 'Repertoire stages and publication receipts are immutable';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER repertoire_resolution_stage_retained BEFORE UPDATE OR DELETE ON normalization.repertoire_resolution_stages
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_repertoire_resolution_stage();
        CREATE TRIGGER repertoire_resolution_associations_immutable BEFORE UPDATE OR DELETE ON normalization.repertoire_resolution_associations
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER repertoire_resolution_registrations_retained BEFORE TRUNCATE ON normalization.repertoire_resolution_registrations
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER repertoire_resolution_attempts_retained BEFORE TRUNCATE ON normalization.repertoire_resolution_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER repertoire_resolution_stages_retained BEFORE TRUNCATE ON normalization.repertoire_resolution_stages
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER repertoire_resolution_associations_retained BEFORE TRUNCATE ON normalization.repertoire_resolution_associations
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain repertoire lineage, source decisions and publication receipts."
        );
}
