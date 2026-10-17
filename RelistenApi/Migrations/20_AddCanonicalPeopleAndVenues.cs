using System;
using SimpleMigrations;

namespace Migrations;

[Migration(20, "Add canonical people, recording credits and physical venues")]
public sealed class AddCanonicalPeopleAndVenues : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.people (
            uuid uuid PRIMARY KEY, display_name text NOT NULL CHECK(length(trim(display_name))>0),
            name_key text NOT NULL, kind text NOT NULL CHECK(kind IN ('person','group')),
            revision bigint NOT NULL DEFAULT 1, updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),metadata_reviewed boolean NOT NULL DEFAULT false
        );
        CREATE INDEX people_name ON normalization.people(name_key,uuid);
        CREATE TABLE normalization.person_aliases (
            uuid uuid PRIMARY KEY,person_uuid uuid NOT NULL REFERENCES normalization.people(uuid),wording text NOT NULL,
            name_key text NOT NULL,input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            json_pointer text NOT NULL,start_code_point integer,end_code_point integer,quote text,
            state text NOT NULL DEFAULT 'accepted' CHECK(state IN ('accepted','pending','rejected','superseded')),
            basis text NOT NULL DEFAULT 'provider_assertion' CHECK(basis IN ('provider_assertion','validated_match','reviewed')),revision bigint NOT NULL DEFAULT 1
        );
        CREATE INDEX person_alias_name ON normalization.person_aliases(name_key,person_uuid);
        CREATE TABLE normalization.person_external_identities (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),person_uuid uuid NOT NULL REFERENCES normalization.people(uuid), provider text NOT NULL,resource_type text NOT NULL,provider_key text NOT NULL,
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),json_pointer text NOT NULL,
            start_code_point integer,end_code_point integer,quote text, state text NOT NULL DEFAULT 'accepted' CHECK(state IN ('accepted','pending','rejected','superseded')),
            basis text NOT NULL DEFAULT 'provider_assertion' CHECK(basis IN ('provider_assertion','validated_match','reviewed')),revision bigint NOT NULL DEFAULT 1
        );
        CREATE TABLE normalization.recording_credits (
            uuid uuid PRIMARY KEY,source_uuid uuid NOT NULL,role text NOT NULL CHECK(role IN ('taper','transferrer','matrix_builder','mastering_engineer','recording_engineer','uploader','collector','musician','other')),
            original_text text NOT NULL CHECK(length(trim(original_text))>0),person_uuid uuid REFERENCES normalization.people(uuid),scope_track_uuid uuid,
            state text NOT NULL CHECK(state IN ('pending','accepted','rejected','superseded')),basis text NOT NULL CHECK(basis IN ('provider_assertion','validated_match','reviewed','title_derived')),
            reason_code text NOT NULL,decision_uuid uuid NOT NULL,revision bigint NOT NULL DEFAULT 1,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE UNIQUE INDEX recording_credit_resolved_identity ON normalization.recording_credits(source_uuid,role,person_uuid,scope_track_uuid) NULLS NOT DISTINCT WHERE state='accepted' AND person_uuid IS NOT NULL;
        CREATE INDEX recording_credits_person ON normalization.recording_credits(person_uuid,source_uuid) WHERE state='accepted';
        CREATE INDEX recording_credits_source ON normalization.recording_credits(source_uuid,state);
        CREATE TABLE normalization.recording_credit_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.recording_credits WITH NO DATA;
        CREATE TRIGGER credit_history_immutable BEFORE UPDATE OR DELETE ON normalization.recording_credit_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.recording_credit_evidence (
            credit_uuid uuid NOT NULL REFERENCES normalization.recording_credits(uuid),input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            json_pointer text NOT NULL,start_code_point integer,end_code_point integer,quote text,credit_revision bigint NOT NULL
        );
        CREATE TABLE normalization.person_redirects (
            retired_uuid uuid PRIMARY KEY REFERENCES normalization.people(uuid),survivor_uuid uuid NOT NULL REFERENCES normalization.people(uuid),
            decision_uuid uuid NOT NULL,CHECK(retired_uuid<>survivor_uuid)
        );
        CREATE TABLE normalization.person_credit_moves (
            decision_uuid uuid NOT NULL,credit_uuid uuid NOT NULL REFERENCES normalization.recording_credits(uuid),
            from_person_uuid uuid NOT NULL REFERENCES normalization.people(uuid),to_person_uuid uuid NOT NULL REFERENCES normalization.people(uuid),
            before_revision bigint NOT NULL,after_revision bigint NOT NULL,PRIMARY KEY(decision_uuid,credit_uuid)
        );
        CREATE TABLE normalization.canonical_venues (
            uuid uuid PRIMARY KEY,display_name text NOT NULL CHECK(length(trim(display_name))>0),name_key text NOT NULL,
            kind text NOT NULL CHECK(kind IN ('site','room')),parent_venue_uuid uuid REFERENCES normalization.canonical_venues(uuid),
            revision bigint NOT NULL DEFAULT 1,updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),metadata_reviewed boolean NOT NULL DEFAULT false,
            CHECK(parent_venue_uuid IS NULL OR (kind='room' AND parent_venue_uuid<>uuid))
        );
        CREATE INDEX canonical_venues_name ON normalization.canonical_venues(name_key,uuid);
        CREATE TABLE normalization.venue_names (
            uuid uuid PRIMARY KEY,venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),wording text NOT NULL,name_key text NOT NULL,
            language text,valid_from date,valid_to date,input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),
            json_pointer text NOT NULL,start_code_point integer,end_code_point integer,quote text,
            state text NOT NULL DEFAULT 'accepted' CHECK(state IN ('accepted','pending','rejected','superseded')),
            basis text NOT NULL DEFAULT 'provider_assertion' CHECK(basis IN ('provider_assertion','validated_match','reviewed')),revision bigint NOT NULL DEFAULT 1,CHECK(valid_from IS NULL OR valid_to IS NULL OR valid_from<=valid_to)
        );
        CREATE INDEX venue_names_key ON normalization.venue_names(name_key,venue_uuid);
        CREATE TABLE normalization.venue_locations (
            uuid uuid PRIMARY KEY,venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),original_locality text NOT NULL,
            country text,region text,city text,address text,latitude double precision,longitude double precision,coordinate_precision text,
            valid_from date,valid_to date,input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),json_pointer text NOT NULL,
            start_code_point integer,end_code_point integer,quote text,
            state text NOT NULL DEFAULT 'accepted' CHECK(state IN ('accepted','pending','rejected','superseded')),
            basis text NOT NULL DEFAULT 'provider_assertion' CHECK(basis IN ('provider_assertion','validated_match','reviewed')),revision bigint NOT NULL DEFAULT 1,CHECK(valid_from IS NULL OR valid_to IS NULL OR valid_from<=valid_to),
            CHECK(latitude BETWEEN -90 AND 90),CHECK(longitude BETWEEN -180 AND 180),CHECK((latitude IS NULL)=(longitude IS NULL)),
            CHECK(latitude IS NULL OR coordinate_precision IS NOT NULL)
        );
        CREATE TABLE normalization.venue_external_identities (
            uuid uuid PRIMARY KEY DEFAULT gen_random_uuid(),venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),provider text NOT NULL,resource_type text NOT NULL,provider_key text NOT NULL,
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),json_pointer text NOT NULL,
            start_code_point integer,end_code_point integer,quote text, state text NOT NULL DEFAULT 'accepted' CHECK(state IN ('accepted','pending','rejected','superseded')),
            basis text NOT NULL DEFAULT 'provider_assertion' CHECK(basis IN ('provider_assertion','validated_match','reviewed')),revision bigint NOT NULL DEFAULT 1
        );
        CREATE TABLE normalization.imported_venue_assignments (
            uuid uuid PRIMARY KEY,imported_venue_uuid uuid NOT NULL,canonical_venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),
            state text NOT NULL CHECK(state IN ('pending','accepted','rejected','superseded')),basis text NOT NULL CHECK(basis IN ('provider_assertion','validated_match','reviewed','title_derived')),
            reason_code text NOT NULL,decision_uuid uuid NOT NULL,revision bigint NOT NULL DEFAULT 1,updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE UNIQUE INDEX imported_venue_current ON normalization.imported_venue_assignments(imported_venue_uuid) WHERE state='accepted';
        CREATE TABLE normalization.show_venue_assignments (
            uuid uuid PRIMARY KEY,show_uuid uuid NOT NULL,canonical_venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),
            state text NOT NULL CHECK(state IN ('pending','accepted','rejected','superseded')),basis text NOT NULL CHECK(basis IN ('provider_assertion','validated_match','reviewed','title_derived')),
            reason_code text NOT NULL,decision_uuid uuid NOT NULL,revision bigint NOT NULL DEFAULT 1,updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE UNIQUE INDEX show_venue_current ON normalization.show_venue_assignments(show_uuid) WHERE state='accepted';
        CREATE TABLE normalization.imported_venue_assignments_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.imported_venue_assignments WITH NO DATA;
        CREATE TABLE normalization.show_venue_assignments_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.show_venue_assignments WITH NO DATA;
        CREATE TRIGGER imported_venue_history_immutable BEFORE UPDATE OR DELETE ON normalization.imported_venue_assignments_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER show_venue_history_immutable BEFORE UPDATE OR DELETE ON normalization.show_venue_assignments_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.venue_assignment_evidence (
            imported_assignment_uuid uuid REFERENCES normalization.imported_venue_assignments(uuid),show_assignment_uuid uuid REFERENCES normalization.show_venue_assignments(uuid),
            input_revision_uuid uuid NOT NULL REFERENCES normalization.input_revisions(uuid),json_pointer text NOT NULL,start_code_point integer,end_code_point integer,quote text,assignment_revision bigint NOT NULL,
            CHECK((imported_assignment_uuid IS NULL)<>(show_assignment_uuid IS NULL))
        );
        CREATE TABLE normalization.venue_redirects (
            retired_uuid uuid PRIMARY KEY REFERENCES normalization.canonical_venues(uuid),survivor_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),decision_uuid uuid NOT NULL,
            CHECK(retired_uuid<>survivor_uuid)
        );
        CREATE TABLE normalization.venue_assignment_moves (
            decision_uuid uuid NOT NULL,imported_assignment_uuid uuid REFERENCES normalization.imported_venue_assignments(uuid),show_assignment_uuid uuid REFERENCES normalization.show_venue_assignments(uuid),
            from_venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),to_venue_uuid uuid NOT NULL REFERENCES normalization.canonical_venues(uuid),
            before_revision bigint NOT NULL,after_revision bigint NOT NULL,CHECK((imported_assignment_uuid IS NULL)<>(show_assignment_uuid IS NULL))
        );
        CREATE TABLE normalization.person_aliases_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.person_aliases WITH NO DATA;
        CREATE TRIGGER person_aliases_history_immutable BEFORE UPDATE OR DELETE ON normalization.person_aliases_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.person_external_identities_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.person_external_identities WITH NO DATA;
        CREATE TRIGGER person_external_identities_history_immutable BEFORE UPDATE OR DELETE ON normalization.person_external_identities_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE UNIQUE INDEX person_external_identities_accepted ON normalization.person_external_identities(provider,resource_type,provider_key) WHERE state='accepted';
        CREATE TABLE normalization.venue_names_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.venue_names WITH NO DATA;
        CREATE TRIGGER venue_names_history_immutable BEFORE UPDATE OR DELETE ON normalization.venue_names_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.venue_locations_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.venue_locations WITH NO DATA;
        CREATE TRIGGER venue_locations_history_immutable BEFORE UPDATE OR DELETE ON normalization.venue_locations_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.venue_external_identities_history AS SELECT *,NULL::uuid AS superseding_decision_uuid FROM normalization.venue_external_identities WITH NO DATA;
        CREATE TRIGGER venue_external_identities_history_immutable BEFORE UPDATE OR DELETE ON normalization.venue_external_identities_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE UNIQUE INDEX venue_external_identities_accepted ON normalization.venue_external_identities(provider,resource_type,provider_key) WHERE state='accepted';
        CREATE TABLE normalization.people_metadata_history AS SELECT *,NULL::uuid AS decision_uuid,NULL::uuid AS input_revision_uuid,NULL::text AS json_pointer FROM normalization.people WITH NO DATA;
        CREATE TRIGGER people_metadata_history_immutable BEFORE UPDATE OR DELETE ON normalization.people_metadata_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TABLE normalization.canonical_venues_metadata_history AS SELECT *,NULL::uuid AS decision_uuid,NULL::uuid AS input_revision_uuid,NULL::text AS json_pointer FROM normalization.canonical_venues WITH NO DATA;
        CREATE TRIGGER canonical_venues_metadata_history_immutable BEFORE UPDATE OR DELETE ON normalization.canonical_venues_metadata_history FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE INDEX person_alias_owner ON normalization.person_aliases(person_uuid,state);
        CREATE INDEX person_external_owner ON normalization.person_external_identities(person_uuid,state);
        CREATE INDEX venue_name_owner ON normalization.venue_names(venue_uuid,state);
        CREATE INDEX venue_location_owner ON normalization.venue_locations(venue_uuid,state);
        CREATE INDEX venue_external_owner ON normalization.venue_external_identities(venue_uuid,state);
        CREATE VIEW normalization.person_identity_resolution AS
            WITH RECURSIVE owners(original_uuid,current_uuid) AS (
                SELECT uuid,uuid FROM normalization.people
                UNION ALL SELECT o.original_uuid,r.survivor_uuid FROM owners o JOIN normalization.person_redirects r ON r.retired_uuid=o.current_uuid
            ) SELECT original_uuid,current_uuid AS resolved_uuid FROM owners o
              WHERE NOT EXISTS(SELECT 1 FROM normalization.person_redirects r WHERE r.retired_uuid=o.current_uuid);
        CREATE VIEW normalization.venue_identity_resolution AS
            WITH RECURSIVE owners(original_uuid,current_uuid) AS (
                SELECT uuid,uuid FROM normalization.canonical_venues
                UNION ALL SELECT o.original_uuid,r.survivor_uuid FROM owners o JOIN normalization.venue_redirects r ON r.retired_uuid=o.current_uuid
            ) SELECT original_uuid,current_uuid AS resolved_uuid FROM owners o
              WHERE NOT EXISTS(SELECT 1 FROM normalization.venue_redirects r WHERE r.retired_uuid=o.current_uuid);
        CREATE VIEW normalization.resolved_person_aliases AS
            SELECT a.*,r.resolved_uuid AS resolved_person_uuid FROM normalization.person_aliases a JOIN normalization.person_identity_resolution r ON r.original_uuid=a.person_uuid WHERE a.state='accepted';
        CREATE VIEW normalization.resolved_person_external_identities AS
            SELECT a.*,r.resolved_uuid AS resolved_person_uuid FROM normalization.person_external_identities a JOIN normalization.person_identity_resolution r ON r.original_uuid=a.person_uuid WHERE a.state='accepted';
        CREATE VIEW normalization.resolved_venue_names AS
            SELECT a.*,r.resolved_uuid AS resolved_venue_uuid FROM normalization.venue_names a JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.venue_uuid WHERE a.state='accepted';
        CREATE VIEW normalization.resolved_venue_locations AS
            SELECT a.*,r.resolved_uuid AS resolved_venue_uuid FROM normalization.venue_locations a JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.venue_uuid WHERE a.state='accepted';
        CREATE VIEW normalization.resolved_venue_external_identities AS
            SELECT a.*,r.resolved_uuid AS resolved_venue_uuid FROM normalization.venue_external_identities a JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.venue_uuid WHERE a.state='accepted';
        CREATE TRIGGER person_moves_immutable BEFORE UPDATE OR DELETE ON normalization.person_credit_moves FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        CREATE TRIGGER venue_moves_immutable BEFORE UPDATE OR DELETE ON normalization.venue_assignment_moves FOR EACH ROW EXECUTE FUNCTION normalization.reject_input_history_mutation();
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain normalization identities and evidence on rollback; disable readers and workers."
        );
}
