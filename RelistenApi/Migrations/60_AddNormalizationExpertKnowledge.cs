using System;
using SimpleMigrations;

namespace Migrations;

[Migration(60, "Retain reviewed scoped normalization correspondence rules")]
public sealed class AddNormalizationExpertKnowledge : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.expert_knowledge_rules (
            rule_uuid uuid PRIMARY KEY,
            subject_uuid uuid NOT NULL UNIQUE REFERENCES normalization.work_subjects(uuid),
            domain text NOT NULL CHECK(domain IN ('venues','songs','compositions','people')),
            revision bigint NOT NULL CHECK(revision>0),
            state text NOT NULL CHECK(state IN ('accepted','rejected','retracted')),
            definition jsonb NOT NULL CHECK(jsonb_typeof(definition)='object'),
            last_operation_uuid uuid NOT NULL REFERENCES normalization.review_operations(uuid)
                DEFERRABLE INITIALLY DEFERRED,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE INDEX expert_knowledge_rule_domain ON normalization.expert_knowledge_rules(domain,rule_uuid);
        CREATE FUNCTION normalization.retain_expert_knowledge_identity() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Expert rules are withdrawn through a retained review, never deleted';
            END IF;
            IF NEW.rule_uuid<>OLD.rule_uuid OR NEW.subject_uuid<>OLD.subject_uuid OR NEW.domain<>OLD.domain
                OR NEW.revision<>OLD.revision+1 THEN
                RAISE EXCEPTION 'Expert rule identity is immutable and revisions advance once';
            END IF;
            IF NEW.state='retracted' AND NEW.definition IS DISTINCT FROM OLD.definition THEN
                RAISE EXCEPTION 'Withdrawal preserves the original expert definition';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER expert_knowledge_identity_retained BEFORE UPDATE OR DELETE ON normalization.expert_knowledge_rules
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_expert_knowledge_identity();
        CREATE FUNCTION normalization.check_expert_knowledge_review() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM normalization.work_subjects subject
                WHERE subject.uuid=NEW.subject_uuid AND subject.domain=NEW.domain
                    AND subject.subject_kind='expert_rule' AND subject.subject_uuid=NEW.rule_uuid) THEN
                RAISE EXCEPTION 'Expert rule requires its registered typed owner';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM normalization.review_operations operation
                WHERE operation.uuid=NEW.last_operation_uuid AND operation.subject_uuid=NEW.subject_uuid
                    AND operation.editorial_revision=NEW.revision
                    AND operation.action=CASE NEW.state WHEN 'accepted' THEN 'accept'
                        WHEN 'rejected' THEN 'reject' ELSE 'retract' END
                    AND (NEW.state='retracted' OR operation.payload->'definition'=NEW.definition)) THEN
                RAISE EXCEPTION 'Expert knowledge requires its matching committed review receipt';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER expert_knowledge_review_receipt
            AFTER INSERT OR UPDATE ON normalization.expert_knowledge_rules
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
            EXECUTE FUNCTION normalization.check_expert_knowledge_review();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain expert correspondence decisions and their review ledger on rollback."
        );
}
