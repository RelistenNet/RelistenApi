using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains explicit item-level performer reviews independently of canonical source linking and artist approval.</summary>
[Migration(15, "Add reviewed Archive item performers")]
public sealed class AddArchivePerformerReviews : Migration
{
    public const string SchemaSql = """
        ALTER TABLE normalization.archive_item_resolutions
            ADD COLUMN performer_candidate_id uuid REFERENCES normalization.archive_artist_candidates(candidate_id),
            ADD COLUMN performer_observation_id uuid REFERENCES normalization.archive_item_observations(observation_id),
            ADD COLUMN performer_approved boolean NOT NULL DEFAULT false,
            ADD COLUMN performer_evidence jsonb NOT NULL DEFAULT '[]',
            ADD COLUMN performer_review_basis text,
            ADD COLUMN performer_alternatives text[] NOT NULL DEFAULT '{}',
            ADD COLUMN performer_revision bigint NOT NULL DEFAULT 0 CHECK(performer_revision>=0),
            ADD CONSTRAINT archive_performer_review_complete CHECK (
                (performer_candidate_id IS NULL AND performer_observation_id IS NULL AND NOT performer_approved
                    AND performer_review_basis IS NULL)
                OR (performer_candidate_id IS NOT NULL AND performer_observation_id IS NOT NULL
                    AND performer_review_basis IS NOT NULL AND jsonb_array_length(performer_evidence)>0)
            );
        CREATE INDEX archive_performer_candidate ON normalization.archive_item_resolutions(performer_candidate_id,item_identifier)
            WHERE performer_candidate_id IS NOT NULL;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep reviewed performer decisions on code rollback. Removal requires a reviewed data migration."
        );
}
