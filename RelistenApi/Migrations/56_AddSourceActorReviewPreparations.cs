using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>One-off trusted source reviews retain immutable preparations; Work retains the committed decisions.</summary>
[Migration(56, "Retain immutable source actor review preparations")]
public sealed class AddSourceActorReviewPreparations : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.source_actor_review_preparations (
            preparation_uuid uuid PRIMARY KEY,
            source_uuid uuid NOT NULL,
            source_owner_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            extraction_attempt_uuid uuid NOT NULL REFERENCES normalization.recording_processing_attempts(attempt_uuid),
            processing_version text NOT NULL CHECK(length(trim(processing_version))>0),
            request_hash text NOT NULL CHECK(request_hash ~ '^[0-9a-f]{64}$'),
            request jsonb NOT NULL CHECK(jsonb_typeof(request)='object'),
            preparation jsonb NOT NULL CHECK(jsonb_typeof(preparation)='object'),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT source_actor_review_preparation_size CHECK(octet_length(preparation::text)<=33554432)
        );
        CREATE INDEX source_actor_review_preparation_source ON normalization.source_actor_review_preparations(source_uuid,created_at,preparation_uuid);
        CREATE TRIGGER source_actor_review_preparation_immutable BEFORE UPDATE OR DELETE
            ON normalization.source_actor_review_preparations FOR EACH ROW
            EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER source_actor_review_preparation_retained BEFORE TRUNCATE
            ON normalization.source_actor_review_preparations FOR EACH STATEMENT
            EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop source review on rollback; retain immutable preparations and trusted decision history."
        );
}
