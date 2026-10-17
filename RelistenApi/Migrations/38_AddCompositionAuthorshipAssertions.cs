using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains source-level author-list completeness separately from individual role credits.</summary>
[Migration(38, "Add evidenced composition authorship assertions")]
public sealed class AddCompositionAuthorshipAssertions : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.composition_authorship_assertions (
            uuid uuid PRIMARY KEY,
            composition_uuid uuid NOT NULL REFERENCES normalization.compositions(uuid),
            complete_authorship boolean NOT NULL,
            authors jsonb NOT NULL CHECK(jsonb_typeof(authors)='array' AND jsonb_array_length(authors) BETWEEN 1 AND 1024),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid)
        );
        CREATE INDEX composition_authorship_current ON normalization.composition_authorship_assertions(composition_uuid,uuid) WHERE NOT retracted;
        CREATE TRIGGER normalization_candidate_matching_revision AFTER INSERT OR UPDATE OR DELETE ON normalization.composition_authorship_assertions
            FOR EACH ROW EXECUTE FUNCTION normalization.record_candidate_matching_revision();
        CREATE TRIGGER normalization_universe_truncate AFTER TRUNCATE ON normalization.composition_authorship_assertions
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_candidate_universe_truncate();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain accepted author assertions and evidence; disable normalized publication on rollback."
        );
}
