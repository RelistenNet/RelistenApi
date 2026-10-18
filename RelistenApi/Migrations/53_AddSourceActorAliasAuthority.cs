using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Canonical alias assertions retain independent reviewed, append and source authority.</summary>
[Migration(53, "Retain source actor alias assignments and current canonical alias authority")]
public sealed class AddSourceActorAliasAuthority : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.source_actor_alias_assignments (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            source_uuid uuid NOT NULL,
            contributor_provider text NOT NULL,contributor_type text NOT NULL,contributor_key text NOT NULL,
            resource_uuid uuid NOT NULL REFERENCES normalization.input_resources(uuid),
            input_revision_uuid uuid NOT NULL,
            field_json_pointer text NOT NULL,producer_version text NOT NULL,
            assertion_uuid uuid NOT NULL,actor_reference_uuid uuid NOT NULL,
            field_sha256 text NOT NULL CHECK(field_sha256 ~ '^[0-9a-f]{64}$'),
            assertion_sha256 text NOT NULL CHECK(assertion_sha256 ~ '^[0-9a-f]{64}$'),
            original_assertion jsonb NOT NULL CHECK(jsonb_typeof(original_assertion)='object'),
            original_scope jsonb NOT NULL CHECK(jsonb_typeof(original_scope)='object'),
            authority_receipt jsonb NOT NULL CHECK(jsonb_typeof(authority_receipt)='object'),
            person_uuid uuid NOT NULL REFERENCES normalization.people(uuid),
            source_resolution_owner_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            extraction_owner_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            extraction_attempt_uuid uuid NOT NULL REFERENCES normalization.recording_processing_attempts(attempt_uuid),
            application_attempt_uuid uuid NOT NULL,stage_uuid uuid NOT NULL,
            basis text NOT NULL CHECK(basis IN ('validated_match','reviewed')),
            introduced_work_uuid uuid NOT NULL REFERENCES normalization.work_runs(uuid),
            retired_work_uuid uuid REFERENCES normalization.work_runs(uuid),
            FOREIGN KEY(resource_uuid,input_revision_uuid)
                REFERENCES normalization.input_revisions(resource_uuid,uuid)
        );
        -- Assertion/reference UUIDs are local to this retained field and producer, not
        -- global actor IDs. Input revision distinguishes claims without losing the stable
        -- source-field coordinates needed to replace a prior revision's support.
        CREATE UNIQUE INDEX source_actor_alias_assignment_current ON normalization.source_actor_alias_assignments
            (source_uuid,contributor_provider,contributor_type,contributor_key,resource_uuid,
             field_json_pointer,producer_version,input_revision_uuid,assertion_uuid)
            WHERE retired_work_uuid IS NULL;
        CREATE INDEX source_actor_alias_assignment_person ON normalization.source_actor_alias_assignments(person_uuid,uuid)
            WHERE retired_work_uuid IS NULL;
        CREATE INDEX source_actor_alias_assignment_field ON normalization.source_actor_alias_assignments
            (source_uuid,contributor_provider,contributor_type,contributor_key,resource_uuid,field_json_pointer,producer_version);

        CREATE TABLE normalization.person_alias_contributor_support (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            assignment_uuid uuid NOT NULL REFERENCES normalization.source_actor_alias_assignments(uuid),
            alias_uuid uuid NOT NULL REFERENCES normalization.person_aliases(uuid),
            introduced_work_uuid uuid NOT NULL REFERENCES normalization.work_runs(uuid),
            retired_work_uuid uuid REFERENCES normalization.work_runs(uuid)
        );
        CREATE UNIQUE INDEX person_alias_current_contributor_support ON normalization.person_alias_contributor_support
            (assignment_uuid,alias_uuid) WHERE retired_work_uuid IS NULL;
        CREATE INDEX person_alias_support_alias ON normalization.person_alias_contributor_support(alias_uuid,assignment_uuid)
            WHERE retired_work_uuid IS NULL;

        CREATE TABLE normalization.person_alias_unmanaged_support (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            alias_uuid uuid NOT NULL UNIQUE REFERENCES normalization.person_aliases(uuid),
            introduced_work_uuid uuid REFERENCES normalization.work_runs(uuid)
        );
        -- Every pre-migration assertion keeps its independent baseline, including pending
        -- or rejected rows. The current projection still requires accepted canonical state.
        -- A null introducing Work records migration provenance, not a fabricated publication.
        INSERT INTO normalization.person_alias_unmanaged_support(alias_uuid)
            SELECT uuid FROM normalization.person_aliases;

        CREATE TABLE normalization.source_actor_alias_review_state (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            source_uuid uuid NOT NULL,
            contributor_provider text NOT NULL,contributor_type text NOT NULL,contributor_key text NOT NULL,
            resource_uuid uuid NOT NULL REFERENCES normalization.input_resources(uuid),
            input_revision_uuid uuid NOT NULL,
            field_json_pointer text NOT NULL,producer_version text NOT NULL,
            assertion_uuid uuid NOT NULL,actor_reference_uuid uuid NOT NULL,
            field_sha256 text NOT NULL CHECK(field_sha256 ~ '^[0-9a-f]{64}$'),
            assertion_sha256 text NOT NULL CHECK(assertion_sha256 ~ '^[0-9a-f]{64}$'),
            original_assertion jsonb NOT NULL CHECK(jsonb_typeof(original_assertion)='object'),
            original_scope jsonb NOT NULL CHECK(jsonb_typeof(original_scope)='object'),
            source_resolution_owner_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            review_revision bigint NOT NULL CHECK(review_revision>0),
            state text NOT NULL CHECK(state IN ('accepted','rejected','retracted')),
            selected_person_uuid uuid REFERENCES normalization.people(uuid),
            last_operation_uuid uuid NOT NULL REFERENCES normalization.review_operations(uuid)
                DEFERRABLE INITIALLY DEFERRED,
            CHECK((state='accepted')=(selected_person_uuid IS NOT NULL)),
            FOREIGN KEY(resource_uuid,input_revision_uuid)
                REFERENCES normalization.input_revisions(resource_uuid,uuid),
            UNIQUE(source_uuid,contributor_provider,contributor_type,contributor_key,resource_uuid,
                field_json_pointer,producer_version,input_revision_uuid,assertion_uuid)
        );
        CREATE INDEX source_actor_alias_review_field ON normalization.source_actor_alias_review_state
            (source_uuid,contributor_provider,contributor_type,contributor_key,resource_uuid,field_json_pointer,producer_version);
        -- The current CAS row is mutable; each accepted transition has an immutable snapshot
        -- in addition to its immutable trusted review_operations decision receipt.
        CREATE TABLE normalization.source_actor_alias_review_state_history
            (LIKE normalization.source_actor_alias_review_state INCLUDING DEFAULTS INCLUDING CONSTRAINTS);
        ALTER TABLE normalization.source_actor_alias_review_state_history ADD PRIMARY KEY(uuid,review_revision);
        CREATE TRIGGER source_actor_alias_review_history_immutable BEFORE UPDATE OR DELETE
            ON normalization.source_actor_alias_review_state_history
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();

        CREATE FUNCTION normalization.check_source_actor_alias_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.retired_work_uuid IS NOT NULL THEN
                RAISE EXCEPTION 'New source alias assignments must be current';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.work_subjects owner
                WHERE owner.uuid=NEW.source_resolution_owner_uuid AND owner.domain='people'
                    AND owner.subject_kind='source_actor_resolution' AND owner.subject_uuid=NEW.source_uuid)
                OR NOT EXISTS(SELECT 1 FROM normalization.work_subjects owner
                    WHERE owner.uuid=NEW.extraction_owner_uuid AND owner.domain='extractions'
                        AND owner.subject_kind='recording' AND owner.subject_uuid=NEW.source_uuid) THEN
                RAISE EXCEPTION 'Source alias assignment requires its original source resolution and extraction owners';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.recording_processing_attempts attempt
                JOIN normalization.work_runs extraction_work ON extraction_work.uuid=attempt.extraction_work_uuid
                WHERE attempt.attempt_uuid=NEW.extraction_attempt_uuid AND attempt.source_uuid=NEW.source_uuid
                    AND attempt.disposition='published' AND extraction_work.state='published'
                    AND extraction_work.subject_uuid=NEW.extraction_owner_uuid) THEN
                RAISE EXCEPTION 'Source alias assignment requires its exact published extraction';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.work_runs work
                JOIN normalization.work_subjects owner ON owner.uuid=work.subject_uuid
                WHERE work.uuid=NEW.introduced_work_uuid AND owner.domain='people'
                    AND owner.subject_kind='person' AND owner.subject_uuid=NEW.person_uuid) THEN
                RAISE EXCEPTION 'Source alias assignment publication must own its original person';
            END IF;
            -- JSON object checks retain proof shape; they do not authenticate extraction or
            -- policy semantics. The future typed accepted writer must verify the full saved
            -- scope/assertion, authority receipt and complete original read set before insert.
            RETURN NEW;
        END $$;
        CREATE TRIGGER source_actor_alias_assignment_proof BEFORE INSERT ON normalization.source_actor_alias_assignments
            FOR EACH ROW EXECUTE FUNCTION normalization.check_source_actor_alias_assignment();

        CREATE FUNCTION normalization.check_person_alias_support() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE original_person uuid;
        BEGIN
            SELECT alias.person_uuid INTO original_person FROM normalization.person_aliases alias
                WHERE alias.uuid=NEW.alias_uuid AND alias.state='accepted';
            IF original_person IS NULL THEN
                RAISE EXCEPTION 'Only an accepted canonical alias can receive new authority';
            END IF;
            IF TG_TABLE_NAME='person_alias_contributor_support' THEN
                IF NEW.retired_work_uuid IS NOT NULL OR NOT EXISTS(
                    SELECT 1 FROM normalization.source_actor_alias_assignments assignment
                    WHERE assignment.uuid=NEW.assignment_uuid AND assignment.person_uuid=original_person
                        AND assignment.retired_work_uuid IS NULL) THEN
                    RAISE EXCEPTION 'New source alias support must belong to the active assignment original person';
                END IF;
            END IF;
            IF NEW.introduced_work_uuid IS NULL OR NOT EXISTS(
                SELECT 1 FROM normalization.work_runs work
                JOIN normalization.work_subjects owner ON owner.uuid=work.subject_uuid
                WHERE work.uuid=NEW.introduced_work_uuid AND owner.domain='people'
                    AND owner.subject_kind='person' AND owner.subject_uuid=original_person) THEN
                RAISE EXCEPTION 'Alias support publication must own its original person';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER person_alias_contributor_support_proof BEFORE INSERT ON normalization.person_alias_contributor_support
            FOR EACH ROW EXECUTE FUNCTION normalization.check_person_alias_support();
        CREATE TRIGGER person_alias_unmanaged_support_proof BEFORE INSERT ON normalization.person_alias_unmanaged_support
            FOR EACH ROW EXECUTE FUNCTION normalization.check_person_alias_support();

        CREATE FUNCTION normalization.retain_source_actor_alias_authority() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE original_person uuid;
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Source actor alias authority history is retained';
            END IF;
            IF OLD.retired_work_uuid IS NOT NULL OR NEW.retired_work_uuid IS NULL
                OR (to_jsonb(OLD)-'retired_work_uuid') IS DISTINCT FROM (to_jsonb(NEW)-'retired_work_uuid') THEN
                RAISE EXCEPTION 'Only current alias authority can be retired; coordinates and proof are immutable';
            END IF;
            IF TG_TABLE_NAME='source_actor_alias_assignments' THEN
                original_person:=OLD.person_uuid;
            ELSE
                SELECT person_uuid INTO original_person FROM normalization.person_aliases WHERE uuid=OLD.alias_uuid;
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.work_runs work
                JOIN normalization.work_subjects owner ON owner.uuid=work.subject_uuid
                WHERE work.uuid=NEW.retired_work_uuid AND owner.domain='people'
                    AND owner.subject_kind='person' AND owner.subject_uuid=original_person) THEN
                RAISE EXCEPTION 'Alias authority retirement must own its original person';
            END IF;
            -- Retirement changes only authority. It never writes a canonical metadata-review
            -- retraction or erases the original alias assertion/citation.
            RETURN NEW;
        END $$;
        DO $$ DECLARE relation text; BEGIN
            FOREACH relation IN ARRAY ARRAY['source_actor_alias_assignments','person_alias_contributor_support'] LOOP
                EXECUTE format('CREATE TRIGGER source_actor_alias_authority_retained BEFORE UPDATE OR DELETE ON normalization.%I FOR EACH ROW EXECUTE FUNCTION normalization.retain_source_actor_alias_authority()',relation);
            END LOOP;
        END $$;
        CREATE TRIGGER person_alias_unmanaged_support_retained BEFORE UPDATE OR DELETE
            ON normalization.person_alias_unmanaged_support
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();

        CREATE FUNCTION normalization.retain_source_actor_alias_review_state() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Source actor alias review state is retained';
            END IF;
            IF TG_OP='INSERT' THEN
                IF NEW.review_revision<>1 THEN
                    RAISE EXCEPTION 'First source alias review must compare against absent revision zero';
                END IF;
            ELSIF (to_jsonb(OLD)-ARRAY['review_revision','state','selected_person_uuid','last_operation_uuid'])
                    IS DISTINCT FROM (to_jsonb(NEW)-ARRAY['review_revision','state','selected_person_uuid','last_operation_uuid'])
                OR NEW.review_revision<>OLD.review_revision+1 OR NEW.last_operation_uuid=OLD.last_operation_uuid THEN
                RAISE EXCEPTION 'Source alias review requires immutable proof and the next CAS revision';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.work_subjects owner
                WHERE owner.uuid=NEW.source_resolution_owner_uuid AND owner.domain='people'
                    AND owner.subject_kind='source_actor_resolution' AND owner.subject_uuid=NEW.source_uuid) THEN
                RAISE EXCEPTION 'Source alias review must name its original source resolution owner';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER source_actor_alias_review_state_guard BEFORE INSERT OR UPDATE OR DELETE
            ON normalization.source_actor_alias_review_state
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_source_actor_alias_review_state();
        -- Work.ApplyReviewAsync invokes the domain callback before inserting its immutable
        -- decision receipt. Enforce receipt ownership/action at transaction completion while
        -- retaining immediate immutable-coordinate and monotonic-revision CAS guards above.
        -- Work's existing "replace" action represents an explicitly accepted reassignment;
        -- a future source review service must translate its domain "reassign" command.
        CREATE FUNCTION normalization.check_source_actor_alias_review_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE action text;
        BEGIN
            SELECT operation.action INTO action FROM normalization.review_operations operation
                WHERE operation.uuid=NEW.last_operation_uuid AND operation.subject_uuid=NEW.source_resolution_owner_uuid;
            IF action IS NULL OR (NEW.state='accepted' AND action NOT IN ('accept','replace'))
                OR (NEW.state='rejected' AND action<>'reject') OR (NEW.state='retracted' AND action<>'retract') THEN
                RAISE EXCEPTION 'Source alias review CAS requires its matching immutable trusted decision';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER source_actor_alias_review_receipt AFTER INSERT OR UPDATE
            ON normalization.source_actor_alias_review_state DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION normalization.check_source_actor_alias_review_receipt();
        CREATE FUNCTION normalization.record_source_actor_alias_review_state() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            INSERT INTO normalization.source_actor_alias_review_state_history SELECT NEW.*;
            RETURN NULL;
        END $$;
        CREATE TRIGGER source_actor_alias_review_state_history AFTER INSERT OR UPDATE
            ON normalization.source_actor_alias_review_state
            FOR EACH ROW EXECUTE FUNCTION normalization.record_source_actor_alias_review_state();

        CREATE VIEW normalization.current_person_aliases AS
            SELECT alias.* FROM normalization.person_aliases alias
            WHERE alias.state='accepted' AND (
                alias.basis='reviewed'
                OR EXISTS(SELECT 1 FROM normalization.person_alias_unmanaged_support support
                    WHERE support.alias_uuid=alias.uuid)
                OR EXISTS(SELECT 1 FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    WHERE support.alias_uuid=alias.uuid AND support.retired_work_uuid IS NULL
                        AND assignment.retired_work_uuid IS NULL)
            );
        CREATE OR REPLACE VIEW normalization.resolved_person_aliases AS
            SELECT alias.*,resolution.resolved_uuid AS resolved_person_uuid
            FROM normalization.current_person_aliases alias
            JOIN normalization.person_identity_resolution resolution ON resolution.original_uuid=alias.person_uuid;

        -- Publication declares People and Compositions write scopes before resource/owner
        -- locks, as existing identity writers do. These statement callbacks use those held
        -- scopes; they do not lock candidate scopes or owners in an alternate order.
        -- Managed authority updates each original person once per statement, which invokes
        -- the existing People/Compositions matching-universe revision trigger. Its future
        -- typed accepted writer must not separately increment this identity revision.
        CREATE FUNCTION normalization.record_person_alias_authority_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE person_uuids uuid[];
        BEGIN
            IF TG_TABLE_NAME='source_actor_alias_assignments' THEN
                SELECT array_agg(DISTINCT person_uuid) INTO person_uuids FROM new_authorities;
            ELSE
                SELECT array_agg(DISTINCT alias.person_uuid) INTO person_uuids
                    FROM new_authorities authority JOIN normalization.person_aliases alias ON alias.uuid=authority.alias_uuid;
            END IF;
            -- Authority membership is a conservative freshness boundary even if another
            -- authority keeps the same spelling visible. No per-support identity updates.
            UPDATE normalization.people SET revision=revision+1,updated_at=clock_timestamp()
                WHERE uuid=ANY(person_uuids);
            RETURN NULL;
        END $$;
        -- Existing append publication already owns the person revision, including its
        -- newly-created-person exception. Unmanaged authority is append-only: its callback
        -- advances matching clocks only, so that path does not double-increment identities.
        CREATE FUNCTION normalization.record_person_alias_unmanaged_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF EXISTS(SELECT 1 FROM new_authorities) THEN
                PERFORM normalization.bump_candidate_universes(ARRAY['compositions','people'],
                    ARRAY['00000000-0000-0000-0000-000000000000'::uuid,
                        '00000000-0000-0000-0000-000000000000'::uuid]);
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER person_alias_unmanaged_insert_revision AFTER INSERT ON normalization.person_alias_unmanaged_support
            REFERENCING NEW TABLE AS new_authorities FOR EACH STATEMENT
            EXECUTE FUNCTION normalization.record_person_alias_unmanaged_revision();
        DO $$ DECLARE relation text; BEGIN
            FOREACH relation IN ARRAY ARRAY['source_actor_alias_assignments','person_alias_contributor_support'] LOOP
                EXECUTE format('CREATE TRIGGER person_alias_authority_insert_revision AFTER INSERT ON normalization.%I REFERENCING NEW TABLE AS new_authorities FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_person_alias_authority_revision()',relation);
                EXECUTE format('CREATE TRIGGER person_alias_authority_retire_revision AFTER UPDATE ON normalization.%I REFERENCING NEW TABLE AS new_authorities FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_person_alias_authority_revision()',relation);
            END LOOP;
            FOREACH relation IN ARRAY ARRAY['source_actor_alias_assignments','person_alias_contributor_support',
                'person_alias_unmanaged_support','source_actor_alias_review_state','source_actor_alias_review_state_history'] LOOP
                EXECUTE format('CREATE TRIGGER source_actor_alias_history_retained BEFORE TRUNCATE ON normalization.%I FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation()',relation);
            END LOOP;
        END $$;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain source actor alias assertions, authority and review history; disable acceptance on rollback."
        );
}
