using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Coordinates typed domain publications without making workflow history the decision ledger.</summary>
[Migration(19, "Coordinate normalization publications and reviewed decisions")]
public sealed class AddNormalizationWork : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.work_subjects (
            uuid uuid PRIMARY KEY,
            domain text NOT NULL CHECK (domain IN ('venues','songs','compositions','people')),
            subject_kind text NOT NULL CHECK (length(trim(subject_kind))>0),
            subject_uuid uuid NOT NULL,
            desired_generation bigint NOT NULL DEFAULT 1 CHECK (desired_generation>0),
            editorial_revision bigint NOT NULL DEFAULT 0 CHECK (editorial_revision>=0),
            policy_version text NOT NULL,
            configuration_hash text NOT NULL,
            published_generation bigint NOT NULL DEFAULT 0,
            published_work_uuid uuid,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(domain,subject_kind,subject_uuid)
        );
        CREATE TABLE normalization.work_inputs (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            resource_uuid uuid NOT NULL REFERENCES normalization.input_resources(uuid),
            published_generation bigint,
            PRIMARY KEY(subject_uuid,resource_uuid)
        );
        CREATE TABLE normalization.work_dependencies (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            dependency_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            published_generation bigint,
            published_editorial_revision bigint,
            published_work_uuid uuid,
            PRIMARY KEY(subject_uuid,dependency_uuid),
            CHECK (subject_uuid<>dependency_uuid)
        );
        CREATE INDEX work_dependencies_reverse ON normalization.work_dependencies(dependency_uuid,subject_uuid);
        CREATE TABLE normalization.work_runs (
            uuid uuid PRIMARY KEY,
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            request_hash text NOT NULL,
            ticket jsonb NOT NULL,
            state text NOT NULL DEFAULT 'running' CHECK (state IN ('running','published','stale')),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            completed_at timestamptz
        );
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_published_run
            FOREIGN KEY(published_work_uuid) REFERENCES normalization.work_runs(uuid);
        CREATE TABLE normalization.review_operations (
            uuid uuid PRIMARY KEY,
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            command_hash text NOT NULL,
            additional_subject_uuids uuid[] NOT NULL DEFAULT '{}',
            action text NOT NULL CHECK (action IN ('accept','reject','replace','retract','merge','split')),
            payload jsonb NOT NULL,
            reason text NOT NULL CHECK (length(trim(reason))>0),
            operator_name text NOT NULL CHECK (length(trim(operator_name))>0),
            editorial_revision bigint NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TRIGGER review_operations_immutable BEFORE UPDATE OR DELETE ON normalization.review_operations
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep normalization decisions and revisions on rollback; stop workers instead."
        );
}
