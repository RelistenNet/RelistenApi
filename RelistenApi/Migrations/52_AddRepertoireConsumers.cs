using System;
using SimpleMigrations;

namespace Migrations;

[Migration(52, "Retain repertoire consumer images, correspondence and authority")]
public sealed class AddRepertoireConsumers : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.music_materialization_policies(
            uuid uuid PRIMARY KEY,artifact_hash text NOT NULL UNIQUE,artifact jsonb NOT NULL,
            evaluation_uuid uuid NOT NULL UNIQUE,evaluation_hash text NOT NULL UNIQUE,evaluation jsonb NOT NULL,
            registered_by text NOT NULL,registered_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE normalization.music_materialization_active(
            policy_key_uuid uuid PRIMARY KEY,artifact_uuid uuid REFERENCES normalization.music_materialization_policies(uuid)
        );
        CREATE TRIGGER music_materialization_policies_immutable BEFORE UPDATE OR DELETE ON normalization.music_materialization_policies
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER music_materialization_policies_retained BEFORE TRUNCATE ON normalization.music_materialization_policies
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.setlist_materialization_registrations(
            registration_uuid uuid PRIMARY KEY,processing_registration_uuid uuid NOT NULL UNIQUE,current_attempt_uuid uuid,edition_uuid uuid,observation_retired boolean NOT NULL DEFAULT false
        );
        CREATE TABLE normalization.setlist_materialization_attempts(
            attempt_uuid uuid PRIMARY KEY,sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
            registration_uuid uuid NOT NULL REFERENCES normalization.setlist_materialization_registrations(registration_uuid),registration jsonb NOT NULL,
            request jsonb,retry_selection jsonb,previous_attempt_uuid uuid REFERENCES normalization.setlist_materialization_attempts(attempt_uuid),
            selected_repertoire_attempt_uuid uuid REFERENCES normalization.repertoire_resolution_attempts(attempt_uuid),
            processing_version text NOT NULL,preparation jsonb,ticket jsonb,receipt jsonb,result jsonb,
            admission_failure_reason text,admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),retirement_work_uuid uuid,cleanup_retry_requirement jsonb,
            checkpoint_revision bigint NOT NULL DEFAULT 0 CHECK(checkpoint_revision>=0),created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(num_nonnulls(request,retry_selection)=1),CHECK(previous_attempt_uuid IS NULL OR previous_attempt_uuid<>attempt_uuid),
            CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=attempt_uuid),
            CHECK(receipt IS NULL OR ticket IS NOT NULL),CHECK(receipt IS NULL OR admission_failure_reason IS NULL),
            CHECK(preparation IS NULL OR selected_repertoire_attempt_uuid IS NOT NULL)
        );
        ALTER TABLE normalization.setlist_materialization_registrations ADD FOREIGN KEY(current_attempt_uuid)
            REFERENCES normalization.setlist_materialization_attempts(attempt_uuid);
        CREATE INDEX setlist_materialization_registration_attempts ON normalization.setlist_materialization_attempts(registration_uuid,sequence DESC);
        CREATE INDEX setlist_materialization_previous_attempt ON normalization.setlist_materialization_attempts(previous_attempt_uuid);
        CREATE TABLE normalization.setlist_materialization_decisions(
            uuid uuid PRIMARY KEY,attempt_uuid uuid NOT NULL REFERENCES normalization.setlist_materialization_attempts(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0),decision_key text NOT NULL,decision jsonb NOT NULL,
            UNIQUE(attempt_uuid,ordinal),UNIQUE(attempt_uuid,decision_key),UNIQUE(uuid,attempt_uuid)
        );
        CREATE FUNCTION normalization.retain_setlist_materialization_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.attempt_uuid,OLD.sequence,OLD.registration_uuid,OLD.registration,OLD.request,OLD.retry_selection,OLD.previous_attempt_uuid,OLD.processing_version,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.sequence,NEW.registration_uuid,NEW.registration,NEW.request,NEW.retry_selection,NEW.previous_attempt_uuid,NEW.processing_version,NEW.created_at)
                OR OLD.result IS NOT NULL OR NEW.checkpoint_revision<>OLD.checkpoint_revision+1
                OR (OLD.selected_repertoire_attempt_uuid IS NOT NULL AND OLD.selected_repertoire_attempt_uuid IS DISTINCT FROM NEW.selected_repertoire_attempt_uuid)
                OR (OLD.preparation IS NOT NULL AND OLD.preparation IS DISTINCT FROM NEW.preparation)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND OLD.receipt IS DISTINCT FROM NEW.receipt)
                OR (OLD.admission_failure_reason IS NOT NULL AND OLD.admission_failure_reason IS DISTINCT FROM NEW.admission_failure_reason) THEN
                RAISE EXCEPTION 'Materialization original requests, selections, preparations and receipts are retained';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER setlist_materialization_attempt_retained BEFORE UPDATE OR DELETE ON normalization.setlist_materialization_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_setlist_materialization_attempt();
        CREATE FUNCTION normalization.retain_setlist_materialization_registration() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.registration_uuid,OLD.processing_registration_uuid) IS DISTINCT FROM (NEW.registration_uuid,NEW.processing_registration_uuid) THEN
                RAISE EXCEPTION 'Materialization source registrations are retained';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER setlist_materialization_registration_retained BEFORE UPDATE OR DELETE ON normalization.setlist_materialization_registrations
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_setlist_materialization_registration();
        CREATE TRIGGER setlist_materialization_decisions_immutable BEFORE UPDATE OR DELETE ON normalization.setlist_materialization_decisions
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_materialization_registrations_retained BEFORE TRUNCATE ON normalization.setlist_materialization_registrations
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_materialization_attempts_retained BEFORE TRUNCATE ON normalization.setlist_materialization_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_materialization_decisions_retained BEFORE TRUNCATE ON normalization.setlist_materialization_decisions
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.recording_content_materialization_registrations(
            registration_uuid uuid PRIMARY KEY,source_uuid uuid NOT NULL UNIQUE,current_attempt_uuid uuid
        );
        CREATE TABLE normalization.recording_content_materialization_attempts(
            attempt_uuid uuid PRIMARY KEY,sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
            registration_uuid uuid NOT NULL REFERENCES normalization.recording_content_materialization_registrations(registration_uuid),registration jsonb NOT NULL,
            request jsonb,retry_selection jsonb,previous_attempt_uuid uuid REFERENCES normalization.recording_content_materialization_attempts(attempt_uuid),
            selected_repertoire_attempt_uuid uuid REFERENCES normalization.repertoire_resolution_attempts(attempt_uuid),
            processing_version text NOT NULL,preparation jsonb,ticket jsonb,receipt jsonb,result jsonb,
            admission_failure_reason text,admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            checkpoint_revision bigint NOT NULL DEFAULT 0 CHECK(checkpoint_revision>=0),created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(num_nonnulls(request,retry_selection)=1),CHECK(previous_attempt_uuid IS NULL OR previous_attempt_uuid<>attempt_uuid),
            CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=attempt_uuid),
            CHECK(receipt IS NULL OR ticket IS NOT NULL),CHECK(receipt IS NULL OR admission_failure_reason IS NULL),
            CHECK(preparation IS NULL OR selected_repertoire_attempt_uuid IS NOT NULL)
        );
        ALTER TABLE normalization.recording_content_materialization_registrations ADD FOREIGN KEY(current_attempt_uuid)
            REFERENCES normalization.recording_content_materialization_attempts(attempt_uuid);
        CREATE INDEX recording_content_materialization_registration_attempts ON normalization.recording_content_materialization_attempts(registration_uuid,sequence DESC);
        CREATE INDEX recording_content_materialization_previous_attempt ON normalization.recording_content_materialization_attempts(previous_attempt_uuid);
        CREATE TABLE normalization.recording_content_materialization_decisions(
            uuid uuid PRIMARY KEY,attempt_uuid uuid NOT NULL REFERENCES normalization.recording_content_materialization_attempts(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0),decision_key text NOT NULL,decision jsonb NOT NULL,
            UNIQUE(attempt_uuid,ordinal),UNIQUE(attempt_uuid,decision_key),UNIQUE(uuid,attempt_uuid)
        );
        CREATE FUNCTION normalization.retain_recording_content_materialization_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.attempt_uuid,OLD.sequence,OLD.registration_uuid,OLD.registration,OLD.request,OLD.retry_selection,OLD.previous_attempt_uuid,OLD.processing_version,OLD.created_at)
                IS DISTINCT FROM (NEW.attempt_uuid,NEW.sequence,NEW.registration_uuid,NEW.registration,NEW.request,NEW.retry_selection,NEW.previous_attempt_uuid,NEW.processing_version,NEW.created_at)
                OR OLD.result IS NOT NULL OR NEW.checkpoint_revision<>OLD.checkpoint_revision+1
                OR (OLD.selected_repertoire_attempt_uuid IS NOT NULL AND OLD.selected_repertoire_attempt_uuid IS DISTINCT FROM NEW.selected_repertoire_attempt_uuid)
                OR (OLD.preparation IS NOT NULL AND OLD.preparation IS DISTINCT FROM NEW.preparation)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND OLD.receipt IS DISTINCT FROM NEW.receipt)
                OR (OLD.admission_failure_reason IS NOT NULL AND OLD.admission_failure_reason IS DISTINCT FROM NEW.admission_failure_reason) THEN
                RAISE EXCEPTION 'Materialization original requests, selections, preparations and receipts are retained';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_content_materialization_attempt_retained BEFORE UPDATE OR DELETE ON normalization.recording_content_materialization_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_content_materialization_attempt();
        CREATE FUNCTION normalization.retain_recording_content_materialization_registration() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.registration_uuid,OLD.source_uuid) IS DISTINCT FROM (NEW.registration_uuid,NEW.source_uuid) THEN
                RAISE EXCEPTION 'Materialization source registrations are retained';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_content_materialization_registration_retained BEFORE UPDATE OR DELETE ON normalization.recording_content_materialization_registrations
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_content_materialization_registration();
        CREATE TRIGGER recording_content_materialization_decisions_immutable BEFORE UPDATE OR DELETE ON normalization.recording_content_materialization_decisions
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_materialization_registrations_retained BEFORE TRUNCATE ON normalization.recording_content_materialization_registrations
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_materialization_attempts_retained BEFORE TRUNCATE ON normalization.recording_content_materialization_attempts
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_materialization_decisions_retained BEFORE TRUNCATE ON normalization.recording_content_materialization_decisions
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.setlist_materialization_retirements(
            work_uuid uuid PRIMARY KEY,attempt_uuid uuid NOT NULL REFERENCES normalization.setlist_materialization_attempts(attempt_uuid),
            ordinal integer NOT NULL CHECK(ordinal>=0 AND ordinal<1000),observed_contribution_revision bigint NOT NULL CHECK(observed_contribution_revision>0),retirement jsonb NOT NULL,ticket jsonb,receipt jsonb,
            failure_reason text,superseded boolean NOT NULL DEFAULT false,admitted_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            UNIQUE(attempt_uuid,ordinal),CHECK((ticket IS NULL)=(admitted_work_uuid IS NULL)),
            CHECK(admitted_work_uuid IS NULL OR admitted_work_uuid=work_uuid),CHECK(receipt IS NULL OR ticket IS NOT NULL),
            CHECK(receipt IS NULL OR failure_reason IS NULL)
        );
        ALTER TABLE normalization.setlist_materialization_attempts ADD FOREIGN KEY(retirement_work_uuid)
            REFERENCES normalization.setlist_materialization_retirements(work_uuid);
        CREATE FUNCTION normalization.retain_setlist_materialization_retirement() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR (OLD.work_uuid,OLD.attempt_uuid,OLD.ordinal,OLD.observed_contribution_revision,OLD.retirement)
                IS DISTINCT FROM (NEW.work_uuid,NEW.attempt_uuid,NEW.ordinal,NEW.observed_contribution_revision,NEW.retirement)
                OR (OLD.ticket IS NOT NULL AND (OLD.ticket,OLD.admitted_work_uuid) IS DISTINCT FROM (NEW.ticket,NEW.admitted_work_uuid))
                OR (OLD.receipt IS NOT NULL AND OLD.receipt IS DISTINCT FROM NEW.receipt)
                OR (OLD.failure_reason IS NOT NULL AND OLD.failure_reason IS DISTINCT FROM NEW.failure_reason)
                OR (OLD.superseded AND NOT NEW.superseded) THEN RAISE EXCEPTION 'Watcher retirement history is retained'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER setlist_materialization_retirement_retained BEFORE UPDATE OR DELETE ON normalization.setlist_materialization_retirements
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_setlist_materialization_retirement();
        CREATE TRIGGER setlist_materialization_retirements_retained BEFORE TRUNCATE ON normalization.setlist_materialization_retirements
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        ALTER TABLE normalization.setlist_authorization_events ALTER COLUMN operation_uuid DROP NOT NULL;
        ALTER TABLE normalization.setlist_authorization_events ADD COLUMN processor_decision_uuid uuid
            REFERENCES normalization.setlist_materialization_decisions(uuid);
        ALTER TABLE normalization.setlist_authorization_events ADD CHECK(num_nonnulls(operation_uuid,processor_decision_uuid)=1);

        CREATE INDEX music_exclusions_recording_source_track ON normalization.music_exclusions((payload->>'SourceTrackUuid'))
            WHERE payload ? 'SourceTrackUuid';

        CREATE TABLE normalization.recording_content_authorization_events(
            uuid uuid PRIMARY KEY,source_uuid uuid NOT NULL,
            operation_uuid uuid REFERENCES normalization.review_operations(uuid) DEFERRABLE INITIALLY DEFERRED,
            processor_decision_uuid uuid REFERENCES normalization.recording_content_materialization_decisions(uuid),
            authorization_key text NOT NULL,kind text NOT NULL CHECK(kind IN ('correspondence','removal')),
            action text NOT NULL CHECK(action IN ('authorize','block','revoke')),prior_uuid uuid REFERENCES normalization.recording_content_authorization_events(uuid),
            payload jsonb NOT NULL,source_tickets jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(num_nonnulls(operation_uuid,processor_decision_uuid)=1),UNIQUE(uuid,source_uuid,authorization_key)
        );
        CREATE TABLE normalization.recording_content_authorization_heads(
            source_uuid uuid NOT NULL,authorization_key text NOT NULL,authorization_uuid uuid NOT NULL,
            PRIMARY KEY(source_uuid,authorization_key),FOREIGN KEY(authorization_uuid,source_uuid,authorization_key)
                REFERENCES normalization.recording_content_authorization_events(uuid,source_uuid,authorization_key)
        );
        CREATE TABLE normalization.recording_content_removal_authorizations_used(
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            authorization_uuid uuid NOT NULL REFERENCES normalization.recording_content_authorization_events(uuid),target_uuid uuid NOT NULL,
            PRIMARY KEY(revision_uuid,target_uuid),UNIQUE(authorization_uuid,target_uuid)
        );
        CREATE TABLE normalization.recording_content_correspondences(
            uuid uuid PRIMARY KEY,sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
            registration_uuid uuid NOT NULL REFERENCES normalization.recording_content_materialization_registrations(registration_uuid),
            attempt_uuid uuid NOT NULL REFERENCES normalization.recording_content_materialization_attempts(attempt_uuid),
            decision_uuid uuid NOT NULL REFERENCES normalization.recording_content_materialization_decisions(uuid),
            source_uuid uuid NOT NULL,content_uuid uuid NOT NULL REFERENCES normalization.track_contents(uuid),
            source_claim_key text NOT NULL,source_reference jsonb,content_revision bigint NOT NULL CHECK(content_revision>0),
            withdrawn boolean NOT NULL,authorization_uuid uuid REFERENCES normalization.recording_content_authorization_events(uuid),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),UNIQUE(uuid,registration_uuid,source_claim_key)
        );
        CREATE INDEX recording_content_correspondences_source ON normalization.recording_content_correspondences(source_uuid,sequence DESC);
        CREATE TABLE normalization.recording_content_correspondence_heads(
            registration_uuid uuid NOT NULL,source_claim_key text NOT NULL,correspondence_uuid uuid NOT NULL,
            PRIMARY KEY(registration_uuid,source_claim_key),FOREIGN KEY(correspondence_uuid,registration_uuid,source_claim_key)
                REFERENCES normalization.recording_content_correspondences(uuid,registration_uuid,source_claim_key)
        );
        CREATE TRIGGER recording_content_authorization_events_immutable BEFORE UPDATE OR DELETE ON normalization.recording_content_authorization_events
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_authorization_events_retained BEFORE TRUNCATE ON normalization.recording_content_authorization_events
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_removal_authorizations_used_immutable BEFORE UPDATE OR DELETE ON normalization.recording_content_removal_authorizations_used
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_removal_authorizations_used_retained BEFORE TRUNCATE ON normalization.recording_content_removal_authorizations_used
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_correspondences_immutable BEFORE UPDATE OR DELETE ON normalization.recording_content_correspondences
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_correspondences_retained BEFORE TRUNCATE ON normalization.recording_content_correspondences
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_authorization_heads_retained BEFORE TRUNCATE ON normalization.recording_content_authorization_heads
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER recording_content_correspondence_heads_retained BEFORE TRUNCATE ON normalization.recording_content_correspondence_heads
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Pause materializers and retain their images, correspondence and original authority receipts."
        );
}
