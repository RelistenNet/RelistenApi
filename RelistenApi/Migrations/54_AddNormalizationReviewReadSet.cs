using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retain original one-off review proof beside its immutable command hash.</summary>
[Migration(54, "Retain trusted normalization review read sets")]
public sealed class AddNormalizationReviewReadSet : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.review_operations ADD COLUMN read_set jsonb
            CHECK(read_set IS NULL OR jsonb_typeof(read_set)='object');
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain trusted review proofs on rollback; stop the new review callers instead."
        );
}
