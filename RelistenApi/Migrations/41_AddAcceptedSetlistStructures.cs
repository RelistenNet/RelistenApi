using System;
using SimpleMigrations;

namespace Migrations;

[Migration(41, "Retain accepted setlist sets and exact proposal correspondence")]
public sealed class AddAcceptedSetlistStructures : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.setlist_structures (
            uuid uuid PRIMARY KEY,
            edition_uuid uuid NOT NULL UNIQUE REFERENCES normalization.setlist_editions(uuid),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid)
        );
        CREATE TABLE normalization.setlist_sets (
            uuid uuid PRIMARY KEY,
            edition_uuid uuid NOT NULL REFERENCES normalization.setlist_editions(uuid),
            position integer CHECK(position>0),label text,encore_number integer CHECK(encore_number>0),
            provider_identity jsonb,
            retracted boolean NOT NULL DEFAULT false,
            active_slot boolean GENERATED ALWAYS AS (CASE WHEN retracted THEN NULL::boolean ELSE true END) STORED,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            UNIQUE(uuid,edition_uuid),
            UNIQUE(edition_uuid,position,active_slot) DEFERRABLE INITIALLY DEFERRED
        );
        CREATE INDEX setlist_sets_page ON normalization.setlist_sets(edition_uuid,position,uuid) WHERE NOT retracted;
        ALTER TABLE normalization.song_occurrences ADD COLUMN set_uuid uuid;
        ALTER TABLE normalization.song_occurrences ADD CONSTRAINT occurrence_set_edition
            FOREIGN KEY(set_uuid,edition_uuid) REFERENCES normalization.setlist_sets(uuid,edition_uuid);
        CREATE TABLE normalization.setlist_structure_correspondences (
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            source_index integer NOT NULL CHECK(source_index>=0),
            source_attempt_uuid uuid NOT NULL REFERENCES normalization.setlist_processing_attempts(attempt_uuid),
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            account_index integer NOT NULL CHECK(account_index>=0),
            kind text NOT NULL CHECK(kind IN ('set','passage')),
            source_key text NOT NULL,target_uuid uuid NOT NULL,
            edition_uuid uuid NOT NULL REFERENCES normalization.setlist_editions(uuid),
            provider_identity jsonb,
            PRIMARY KEY(revision_uuid,source_index,kind,source_key,target_uuid)
        );
        CREATE INDEX setlist_correspondence_target ON normalization.setlist_structure_correspondences(edition_uuid,kind,target_uuid);
        CREATE INDEX setlist_correspondence_claim ON normalization.setlist_structure_correspondences(edition_uuid,kind,input_revision_uuid,account_index,source_key);
        CREATE INDEX setlist_correspondence_identity ON normalization.setlist_structure_correspondences USING gin(provider_identity jsonb_path_ops) WHERE provider_identity IS NOT NULL;
        CREATE TRIGGER setlist_correspondences_immutable BEFORE UPDATE OR DELETE ON normalization.setlist_structure_correspondences
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER setlist_correspondences_retained BEFORE TRUNCATE ON normalization.setlist_structure_correspondences
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain accepted setlist structures and correspondence; disable normalized structure writes on rollback."
        );
}
