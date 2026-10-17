using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Supports complete assignment authority reads without scanning unrelated venues or evidence history.</summary>
[Migration(40, "Index imported venue resolution context")]
public sealed class AddImportedVenueResolutionIndexes : Migration
{
    public const string SchemaSql = """
        -- The accepted-only unique index cannot serve pending/rejected equivalent copies.
        CREATE INDEX imported_venue_resolution_context
            ON normalization.imported_venue_assignments(imported_venue_uuid,uuid,revision);
        CREATE INDEX imported_venue_reviewed_rejections
            ON normalization.imported_venue_assignments_history(imported_venue_uuid,uuid,revision)
            WHERE state='rejected' AND basis='reviewed';
        CREATE INDEX imported_venue_assignment_revision_evidence
            ON normalization.venue_assignment_evidence(imported_assignment_uuid,assignment_revision)
            WHERE imported_assignment_uuid IS NOT NULL;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable venue resolution readers on rollback; retain their additive indexes."
        );
}
