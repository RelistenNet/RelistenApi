using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Pipeline owners keep delivery pending after extraction acknowledgement until the
/// guarded people, alias and venue stages settle. They own no accepted catalog facts.
/// </summary>
[Migration(59, "Allow durable recording normalization pipeline owners")]
public sealed class AddRecordingNormalizationPipeline : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.work_subjects DROP CONSTRAINT work_extraction_kind;
        ALTER TABLE normalization.work_subjects ADD CONSTRAINT work_extraction_kind
            CHECK(domain<>'extractions' OR subject_kind IN ('recording','setlist','recording_pipeline'));
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain unfinished pipeline owners on rollback; stop the pipeline worker instead of deleting recovery state."
        );
}
