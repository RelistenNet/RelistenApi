using System;
using SimpleMigrations;

namespace Migrations;

[Migration(
    42,
    "Review exact setlist correspondence and removal authority independently of metadata"
)]
public sealed class AddSetlistCorrespondenceAuthorizations : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.setlist_authorization_events (
            uuid uuid PRIMARY KEY,
            edition_uuid uuid NOT NULL REFERENCES normalization.setlist_editions(uuid),
            operation_uuid uuid NOT NULL REFERENCES normalization.review_operations(uuid) DEFERRABLE INITIALLY DEFERRED,
            authorization_key text NOT NULL,
            kind text NOT NULL CHECK(kind IN ('correspondence','removal')),
            action text NOT NULL CHECK(action IN ('authorize','block','revoke')),
            expected_structure_revision bigint NOT NULL CHECK(expected_structure_revision>=0),
            expected_edition_revision bigint NOT NULL CHECK(expected_edition_revision>0),
            prior_uuid uuid REFERENCES normalization.setlist_authorization_events(uuid),
            payload jsonb NOT NULL,
            source_tickets jsonb NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(uuid,edition_uuid,authorization_key)
        );
        CREATE INDEX setlist_authorizations_page ON normalization.setlist_authorization_events(edition_uuid,uuid);
        CREATE TABLE normalization.setlist_authorization_heads (
            edition_uuid uuid NOT NULL,
            authorization_key text NOT NULL,
            authorization_uuid uuid NOT NULL,
            PRIMARY KEY(edition_uuid,authorization_key),
            FOREIGN KEY(authorization_uuid,edition_uuid,authorization_key)
                REFERENCES normalization.setlist_authorization_events(uuid,edition_uuid,authorization_key)
        );
        ALTER TABLE normalization.setlist_structure_correspondences
            ADD COLUMN authorization_uuid uuid REFERENCES normalization.setlist_authorization_events(uuid);
        CREATE TABLE normalization.setlist_removal_authorizations_used (
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            authorization_uuid uuid NOT NULL REFERENCES normalization.setlist_authorization_events(uuid),
            kind text NOT NULL CHECK(kind IN ('set','occurrence','edition')),
            target_uuid uuid NOT NULL,
            PRIMARY KEY(revision_uuid,kind,target_uuid)
        );
        CREATE TRIGGER setlist_authorizations_immutable BEFORE UPDATE OR DELETE ON normalization.setlist_authorization_events
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_authorizations_retained BEFORE TRUNCATE ON normalization.setlist_authorization_events
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_removal_authorizations_immutable BEFORE UPDATE OR DELETE ON normalization.setlist_removal_authorizations_used
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_removal_authorizations_retained BEFORE TRUNCATE ON normalization.setlist_removal_authorizations_used
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain reviewed setlist authorization and consumption history; disable its writers on rollback."
        );
}
