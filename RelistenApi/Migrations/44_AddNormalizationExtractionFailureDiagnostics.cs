using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Keep failed response evidence on its attempt, separate from reusable extraction results.</summary>
[Migration(44, "Retain bounded normalization extraction failure diagnostics")]
public sealed class AddNormalizationExtractionFailureDiagnostics : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.extraction_attempts
            ADD COLUMN failure_stage text,
            ADD COLUMN failed_response_body bytea,
            ADD CONSTRAINT extraction_failure_stage CHECK (
                failure_stage IS NULL OR (
                    state='failed' AND failure_stage IN (
                        'response_envelope','credit_schema','credit_support','usage_limit'
                    )
                )
            ),
            ADD CONSTRAINT extraction_failure_body CHECK (
                failed_response_body IS NULL OR (
                    failure_stage IS NOT NULL AND octet_length(failed_response_body)<=1048576
                )
            );
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep extraction failure evidence on rollback; stop workers instead."
        );
}
