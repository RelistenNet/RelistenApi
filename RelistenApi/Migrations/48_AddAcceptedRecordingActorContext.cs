using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Accepted actor accounts retain contributor ownership without making hints globally unique IDs.</summary>
[Migration(48, "Retain accepted contributor-scoped recording actor context and lookup hints")]
public sealed class AddAcceptedRecordingActorContext : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.recording_actor_contexts (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            support_uuid uuid NOT NULL REFERENCES normalization.recording_credit_contributor_support(uuid),
            credit_uuid uuid NOT NULL REFERENCES normalization.recording_credits(uuid),
            credit_revision bigint NOT NULL CHECK(credit_revision>0),credit_basis text NOT NULL,
            person_uuid uuid NOT NULL REFERENCES normalization.people(uuid),source_uuid uuid NOT NULL,
            owner_subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            contributor_provider text NOT NULL,contributor_type text NOT NULL,contributor_key text NOT NULL,
            extraction_attempt_uuid uuid NOT NULL REFERENCES normalization.recording_processing_attempts(attempt_uuid),
            proposal_uuid uuid NOT NULL,input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            application_attempt_uuid uuid NOT NULL REFERENCES normalization.people_acceptance_applications(attempt_uuid),
            introduced_work_uuid uuid NOT NULL REFERENCES normalization.work_runs(uuid),
            retired_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            basis text NOT NULL CHECK(basis='validated_match'),credited_name text NOT NULL,
            facts_hash text NOT NULL,facts jsonb NOT NULL,
            UNIQUE(support_uuid,proposal_uuid,person_uuid)
        );
        CREATE INDEX recording_actor_context_person ON normalization.recording_actor_contexts(person_uuid,uuid)
            WHERE retired_work_uuid IS NULL;
        CREATE INDEX recording_actor_context_source ON normalization.recording_actor_contexts(source_uuid,uuid);
        CREATE INDEX recording_actor_context_support ON normalization.recording_actor_contexts(support_uuid)
            WHERE retired_work_uuid IS NULL;

        -- These projections are disposable lookup indexes. The original typed facts and their
        -- evidence remain in the immutable context even when its support is retired.
        CREATE TABLE normalization.recording_actor_context_aliases (
            context_uuid uuid NOT NULL REFERENCES normalization.recording_actor_contexts(uuid),
            fact_uuid uuid NOT NULL,wording text NOT NULL,name_key text NOT NULL,
            PRIMARY KEY(context_uuid,fact_uuid)
        );
        CREATE INDEX recording_actor_alias_name ON normalization.recording_actor_context_aliases
            USING gist(name_key gist_trgm_ops(siglen=256));
        CREATE TABLE normalization.recording_actor_context_contacts (
            context_uuid uuid NOT NULL REFERENCES normalization.recording_actor_contexts(uuid),
            fact_uuid uuid NOT NULL,contact_type text NOT NULL,lookup_key text NOT NULL,
            PRIMARY KEY(context_uuid,fact_uuid)
        );
        CREATE INDEX recording_actor_contact_key ON normalization.recording_actor_context_contacts(contact_type,lookup_key,context_uuid);
        CREATE TABLE normalization.recording_actor_context_profiles (
            context_uuid uuid NOT NULL REFERENCES normalization.recording_actor_contexts(uuid),
            fact_uuid uuid NOT NULL,provider text NOT NULL,namespace text NOT NULL,lookup_key text NOT NULL,
            PRIMARY KEY(context_uuid,fact_uuid)
        );
        CREATE INDEX recording_actor_profile_key ON normalization.recording_actor_context_profiles(provider,namespace,lookup_key,context_uuid);

        CREATE FUNCTION normalization.record_actor_context_matching_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- Accepted writers declare/lock the people candidate scope before owner/domain
            -- locks. These callbacks advance that already-held clock even when a credit row
            -- itself did not change, including support retirement or lookup-only maintenance.
            PERFORM normalization.bump_candidate_universes(ARRAY['people'],
                ARRAY['00000000-0000-0000-0000-000000000000'::uuid]);
            RETURN NULL;
        END $$;
        DO $$ DECLARE relation text; BEGIN
            FOREACH relation IN ARRAY ARRAY['recording_actor_contexts','recording_actor_context_aliases',
                'recording_actor_context_contacts','recording_actor_context_profiles','recording_credit_contributor_support'] LOOP
                EXECUTE format('CREATE TRIGGER recording_actor_context_candidate_revision AFTER INSERT OR UPDATE OR DELETE ON normalization.%I FOR EACH ROW EXECUTE FUNCTION normalization.record_actor_context_matching_revision()',relation);
            END LOOP;
            FOREACH relation IN ARRAY ARRAY['recording_actor_context_aliases','recording_actor_context_contacts',
                'recording_actor_context_profiles'] LOOP
                EXECUTE format('CREATE TRIGGER recording_actor_lookup_truncate AFTER TRUNCATE ON normalization.%I FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_actor_context_matching_revision()',relation);
            END LOOP;
        END $$;

        CREATE FUNCTION normalization.retain_recording_actor_context() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.retired_work_uuid IS NOT NULL OR NEW.retired_work_uuid IS NULL
                OR (to_jsonb(OLD)-'retired_work_uuid') IS DISTINCT FROM (to_jsonb(NEW)-'retired_work_uuid') THEN
                RAISE EXCEPTION 'Accepted actor context is immutable except for contributor retirement';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.recording_credit_contributor_support support
                WHERE support.uuid=OLD.support_uuid AND support.retired_work_uuid=NEW.retired_work_uuid) THEN
                RAISE EXCEPTION 'Actor context retirement must follow its exact contributor support';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER recording_actor_context_retained BEFORE UPDATE OR DELETE ON normalization.recording_actor_contexts
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_recording_actor_context();
        CREATE TRIGGER recording_actor_context_history_retained BEFORE TRUNCATE ON normalization.recording_actor_contexts
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain accepted actor evidence and contributor retirement history."
        );
}
