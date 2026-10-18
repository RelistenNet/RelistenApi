using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Bound cumulative paid admission independently of token spend and per-request retries.</summary>
[Migration(45, "Bound normalization extraction attempts per job")]
public sealed class AddNormalizationExtractionJobAttemptLimit : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.extraction_jobs
            ADD COLUMN maximum_total_attempts integer
                CHECK(maximum_total_attempts IS NULL OR maximum_total_attempts>0);
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep extraction admission limits on rollback; stop workers instead."
        );
}
