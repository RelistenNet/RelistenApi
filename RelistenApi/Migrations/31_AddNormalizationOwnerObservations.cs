using System;
using SimpleMigrations;

namespace Migrations;

[Migration(
    31,
    "Retain normalization observational owner read sets separately from causal dependencies"
)]
public sealed class AddNormalizationOwnerObservations : Migration
{
    public const string SchemaSql = """
        -- Observing a recording's credit context from its person owner must not install the
        -- reverse of the recording -> person causal edge. Self observations are also valid.
        CREATE TABLE normalization.work_owner_observations (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            observed_subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            published_desired_generation bigint CHECK(published_desired_generation>=0),
            published_editorial_revision bigint CHECK(published_editorial_revision>=0),
            PRIMARY KEY(subject_uuid,observed_subject_uuid)
        );
        CREATE TABLE normalization.work_contribution_owner_observations (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            observed_subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            PRIMARY KEY(contribution_uuid,observed_subject_uuid)
        );
        -- Complete original stamps, including published_work_uuid, remain immutable in work
        -- tickets. Pending deliberately ignores receipt-only changes: candidate epochs fence
        -- semantic facts, while desired/editorial revisions fence configuration and review.
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Stop normalization publication before rollback; retain observational read sets and contribution ownership."
        );
}
