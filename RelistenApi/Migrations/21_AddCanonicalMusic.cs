using System;
using SimpleMigrations;

namespace Migrations;

[Migration(21, "Add repertoire compositions ordered passages and file contents")]
public sealed class AddCanonicalMusic : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.music_exclusions (
            claim_hash text NOT NULL,
            decision_uuid uuid NOT NULL,
            payload jsonb NOT NULL,
            PRIMARY KEY(claim_hash,decision_uuid)
        );
        CREATE TABLE normalization.music_exclusion_evidence (
            claim_hash text NOT NULL,
            decision_uuid uuid NOT NULL,
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            FOREIGN KEY(claim_hash,decision_uuid) REFERENCES normalization.music_exclusions(claim_hash,decision_uuid),
            PRIMARY KEY(claim_hash,decision_uuid,input_revision_uuid)
        );
        CREATE TRIGGER music_exclusions_immutable BEFORE UPDATE OR DELETE ON normalization.music_exclusions
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER music_exclusion_evidence_immutable BEFORE UPDATE OR DELETE ON normalization.music_exclusion_evidence
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.music_revisions (
            uuid uuid PRIMARY KEY,
            entity_uuid uuid NOT NULL,
            entity_kind text NOT NULL,
            revision bigint NOT NULL CHECK(revision>0),
            decision_uuid uuid NOT NULL,
            basis text NOT NULL CHECK(basis IN ('provider_assertion','validated_match','reviewed','title_derived')),
            payload jsonb NOT NULL,
            evidence jsonb NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE(entity_kind,entity_uuid,revision)
        );
        CREATE TABLE normalization.music_evidence_references (
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            ordinal integer NOT NULL,
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            json_pointer text NOT NULL,
            start_code_point integer,
            end_code_point integer,
            quote text,
            PRIMARY KEY(revision_uuid,ordinal)
        );
        CREATE TRIGGER music_revisions_immutable BEFORE UPDATE OR DELETE ON normalization.music_revisions
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER music_evidence_immutable BEFORE UPDATE OR DELETE ON normalization.music_evidence_references
            FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();

        CREATE TABLE normalization.repertoire_songs (
            uuid uuid PRIMARY KEY,
            artist_uuid uuid NOT NULL,
            display_title text NOT NULL CHECK(length(trim(display_title))>0),
            aliases text[] NOT NULL DEFAULT '{}',
            redirect_uuid uuid REFERENCES normalization.repertoire_songs(uuid),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK(redirect_uuid IS DISTINCT FROM uuid)
        );
        CREATE INDEX repertoire_redirect ON normalization.repertoire_songs(redirect_uuid) WHERE redirect_uuid IS NOT NULL;
        CREATE INDEX repertoire_title ON normalization.repertoire_songs(artist_uuid,lower(display_title),uuid) WHERE NOT retracted AND redirect_uuid IS NULL;
        CREATE TABLE normalization.compositions (
            uuid uuid PRIMARY KEY,
            display_title text NOT NULL CHECK(length(trim(display_title))>0),
            aliases text[] NOT NULL DEFAULT '{}',
            redirect_uuid uuid REFERENCES normalization.compositions(uuid),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK(redirect_uuid IS DISTINCT FROM uuid)
        );
        CREATE INDEX composition_redirect ON normalization.compositions(redirect_uuid) WHERE redirect_uuid IS NOT NULL;
        CREATE INDEX composition_title ON normalization.compositions(lower(display_title),uuid) WHERE NOT retracted AND redirect_uuid IS NULL;
        CREATE TABLE normalization.imported_song_assignments (
            uuid uuid PRIMARY KEY,
            imported_song_uuid uuid NOT NULL,
            repertoire_song_uuid uuid NOT NULL REFERENCES normalization.repertoire_songs(uuid),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid)
        );
        CREATE UNIQUE INDEX imported_song_current ON normalization.imported_song_assignments(imported_song_uuid) WHERE NOT retracted;
        CREATE TABLE normalization.repertoire_compositions (
            uuid uuid PRIMARY KEY,
            repertoire_song_uuid uuid NOT NULL REFERENCES normalization.repertoire_songs(uuid),
            composition_uuid uuid NOT NULL REFERENCES normalization.compositions(uuid),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid)
        );
        CREATE UNIQUE INDEX repertoire_composition_current ON normalization.repertoire_compositions(repertoire_song_uuid) WHERE NOT retracted;
        CREATE TABLE normalization.work_credits (
            uuid uuid PRIMARY KEY,
            composition_uuid uuid NOT NULL REFERENCES normalization.compositions(uuid),
            role text NOT NULL CHECK(role IN ('writer','composer','lyricist','arranger','cover_artist','original_performer')),
            original_text text NOT NULL,
            person_uuid uuid REFERENCES normalization.people(uuid),
            artist_uuid uuid,
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK(person_uuid IS NULL OR artist_uuid IS NULL)
        );
        CREATE TABLE normalization.setlist_editions (
            uuid uuid PRIMARY KEY,
            artist_uuid uuid NOT NULL,
            show_uuid uuid,
            literal_date text NOT NULL,
            ordered boolean NOT NULL,
            provider text NOT NULL,
            resource_type text NOT NULL,
            provider_key text NOT NULL,
            selected boolean NOT NULL DEFAULT false,
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK(NOT selected OR (show_uuid IS NOT NULL AND NOT retracted)),
            UNIQUE(artist_uuid,provider,resource_type,provider_key)
        );
        CREATE UNIQUE INDEX selected_show_edition ON normalization.setlist_editions(show_uuid) WHERE selected AND NOT retracted;
        CREATE INDEX edition_artist_date ON normalization.setlist_editions(artist_uuid,literal_date DESC,uuid);
        CREATE TABLE normalization.song_occurrences (
            uuid uuid PRIMARY KEY,
            edition_uuid uuid NOT NULL REFERENCES normalization.setlist_editions(uuid),
            position integer CHECK(position>0),
            set_label text,
            kind text NOT NULL CHECK(kind IN ('song','jam','speech','nonmusic','unknown')),
            repertoire_song_uuid uuid REFERENCES normalization.repertoire_songs(uuid),
            original_text text NOT NULL,
            transition_after text NOT NULL CHECK(transition_after IN ('segue','stop','unknown')),
            parent_occurrence_uuid uuid REFERENCES normalization.song_occurrences(uuid),
            component_position integer CHECK(component_position>0),
            supersedes_occurrence_uuid uuid REFERENCES normalization.song_occurrences(uuid),
            provider_item_key text,
            retracted boolean NOT NULL DEFAULT false,
            active_slot boolean GENERATED ALWAYS AS (CASE WHEN retracted THEN NULL::boolean ELSE true END) STORED,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK((parent_occurrence_uuid IS NULL)=(component_position IS NULL)),
            CHECK(parent_occurrence_uuid IS NULL OR position IS NULL),
            CHECK(parent_occurrence_uuid IS DISTINCT FROM uuid),
            CHECK(supersedes_occurrence_uuid IS DISTINCT FROM uuid)
        );
        ALTER TABLE normalization.song_occurrences ADD CONSTRAINT occurrence_edition_position
            UNIQUE(edition_uuid,position,active_slot) DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE normalization.song_occurrences ADD CONSTRAINT occurrence_component_position
            UNIQUE(parent_occurrence_uuid,component_position,active_slot) DEFERRABLE INITIALLY DEFERRED;
        CREATE UNIQUE INDEX occurrence_provider_item ON normalization.song_occurrences(edition_uuid,provider_item_key) WHERE provider_item_key IS NOT NULL AND NOT retracted;
        CREATE TABLE normalization.track_contents (
            uuid uuid PRIMARY KEY,
            source_track_uuid uuid NOT NULL,
            artist_uuid uuid NOT NULL,
            source_uuid uuid NOT NULL,
            position integer NOT NULL CHECK(position>0),
            kind text NOT NULL CHECK(kind IN ('song','jam','speech','nonmusic','unknown')),
            original_text text NOT NULL,
            repertoire_song_uuid uuid REFERENCES normalization.repertoire_songs(uuid),
            occurrence_uuid uuid REFERENCES normalization.song_occurrences(uuid),
            start_ms bigint CHECK(start_ms>=0),
            end_ms bigint CHECK(end_ms>=0),
            match_evidence text NOT NULL CHECK(match_evidence IN ('file_title','recording_metadata')),
            retracted boolean NOT NULL DEFAULT false,
            active_slot boolean GENERATED ALWAYS AS (CASE WHEN retracted THEN NULL::boolean ELSE true END) STORED,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK(start_ms IS NULL OR end_ms IS NULL OR end_ms>=start_ms),
            UNIQUE(source_track_uuid,position,active_slot) DEFERRABLE INITIALLY DEFERRED
        );
        CREATE INDEX contents_repertoire ON normalization.track_contents(repertoire_song_uuid) WHERE NOT retracted;
        CREATE INDEX contents_occurrence ON normalization.track_contents(occurrence_uuid) WHERE NOT retracted;
        CREATE TABLE normalization.music_external_identities (
            uuid uuid PRIMARY KEY,
            repertoire_song_uuid uuid REFERENCES normalization.repertoire_songs(uuid),
            composition_uuid uuid REFERENCES normalization.compositions(uuid),
            artist_uuid uuid,
            provider text NOT NULL CHECK(length(trim(provider))>0),
            resource_type text NOT NULL CHECK(length(trim(resource_type))>0),
            provider_key text NOT NULL CHECK(length(trim(provider_key))>0),
            retracted boolean NOT NULL DEFAULT false,
            revision_uuid uuid NOT NULL REFERENCES normalization.music_revisions(uuid),
            CHECK((repertoire_song_uuid IS NULL)<>(composition_uuid IS NULL)),
            CHECK((repertoire_song_uuid IS NULL)=(artist_uuid IS NULL))
        );
        CREATE UNIQUE INDEX repertoire_external_identity ON normalization.music_external_identities(artist_uuid,provider,resource_type,provider_key)
            WHERE repertoire_song_uuid IS NOT NULL AND NOT retracted;
        CREATE UNIQUE INDEX composition_external_identity ON normalization.music_external_identities(provider,resource_type,provider_key)
            WHERE composition_uuid IS NOT NULL AND NOT retracted;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain accepted music and its evidence; disable normalized reads on rollback."
        );
}
