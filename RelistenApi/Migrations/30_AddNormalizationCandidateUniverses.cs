using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Fences candidate selection, including a new rival or accepted relationship context.
/// Globals cover people, venues and compositions; repertoire is scoped to one artist.
/// </summary>
[Migration(30, "Fence normalization candidate universes and contributed read sets")]
public sealed class AddNormalizationCandidateUniverses : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.candidate_universe_revisions (
            domain text NOT NULL CHECK(domain IN ('people','venues','compositions','repertoire')),
            scope_uuid uuid NOT NULL,
            generation bigint NOT NULL DEFAULT 0 CHECK(generation>=0),
            PRIMARY KEY(domain,scope_uuid),
            CHECK((domain='repertoire')=(scope_uuid<>'00000000-0000-0000-0000-000000000000'::uuid))
        );
        INSERT INTO normalization.candidate_universe_revisions(domain,scope_uuid)
            VALUES('people','00000000-0000-0000-0000-000000000000'),
                ('venues','00000000-0000-0000-0000-000000000000'),
                ('compositions','00000000-0000-0000-0000-000000000000');
        -- Existing positive dictionaries need clocks before any offline TRUNCATE. Empty
        -- artist scopes may remain absent and are read as generation zero until admission.
        INSERT INTO normalization.candidate_universe_revisions(domain,scope_uuid)
            SELECT DISTINCT 'repertoire',artist_uuid FROM normalization.repertoire_songs;
        CREATE TABLE normalization.work_candidate_inputs (
            subject_uuid uuid NOT NULL REFERENCES normalization.work_subjects(uuid),
            domain text NOT NULL,
            scope_uuid uuid NOT NULL,
            published_generation bigint,
            FOREIGN KEY(domain,scope_uuid) REFERENCES normalization.candidate_universe_revisions(domain,scope_uuid),
            PRIMARY KEY(subject_uuid,domain,scope_uuid)
        );
        CREATE TABLE normalization.work_contribution_candidate_inputs (
            contribution_uuid uuid NOT NULL REFERENCES normalization.work_input_contributions(uuid),
            domain text NOT NULL,
            scope_uuid uuid NOT NULL,
            FOREIGN KEY(domain,scope_uuid) REFERENCES normalization.candidate_universe_revisions(domain,scope_uuid),
            PRIMARY KEY(contribution_uuid,domain,scope_uuid)
        );

        CREATE FUNCTION normalization.bump_candidate_universes(candidate_domains text[],candidate_scopes uuid[]) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE universe record;
        BEGIN
            IF cardinality(candidate_domains) IS DISTINCT FROM cardinality(candidate_scopes) THEN
                RAISE EXCEPTION 'Candidate universe domains and scopes must have equal lengths';
            END IF;
            FOR universe IN SELECT DISTINCT keys.domain COLLATE "C" AS domain,keys.scope_uuid
                    FROM unnest(candidate_domains,candidate_scopes) keys(domain,scope_uuid)
                    ORDER BY domain,scope_uuid LOOP
                INSERT INTO normalization.candidate_universe_revisions(domain,scope_uuid,generation)
                    VALUES(universe.domain,universe.scope_uuid,1)
                    ON CONFLICT(domain,scope_uuid) DO UPDATE SET generation=candidate_universe_revisions.generation+1;
            END LOOP;
        END $$;

        CREATE FUNCTION normalization.retain_candidate_universe_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Retain candidate universe revisions; disable normalization workers for rollback';
        END $$;
        CREATE TRIGGER candidate_universe_revision_retained BEFORE DELETE ON normalization.candidate_universe_revisions
            FOR EACH ROW EXECUTE FUNCTION normalization.retain_candidate_universe_revision();
        CREATE TRIGGER candidate_universe_revisions_retained BEFORE TRUNCATE ON normalization.candidate_universe_revisions
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.retain_candidate_universe_revision();

        CREATE FUNCTION normalization.record_candidate_matching_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            old_row jsonb:='{}'; new_row jsonb:='{}'; row_value jsonb;
            candidate_domains text[]:='{}'; candidate_scopes uuid[]:='{}';
            artist_keys uuid[]:='{}'; artist_key uuid; song_keys uuid[]:='{}';
            global_scope constant uuid:='00000000-0000-0000-0000-000000000000';
        BEGIN
            IF TG_OP<>'INSERT' THEN old_row:=to_jsonb(OLD); END IF;
            IF TG_OP<>'DELETE' THEN new_row:=to_jsonb(NEW); END IF;
            IF TG_OP='UPDATE' AND (old_row-ARRAY['created_at','updated_at'])
                    IS NOT DISTINCT FROM (new_row-ARRAY['created_at','updated_at']) THEN
                RETURN NULL;
            END IF;

            IF TG_TABLE_NAME IN ('people','person_aliases','person_external_identities','person_redirects') THEN
                candidate_domains:=ARRAY['compositions','people']; candidate_scopes:=ARRAY[global_scope,global_scope];
            ELSIF TG_TABLE_NAME IN ('recording_credits','recording_credit_evidence') THEN
                candidate_domains:=ARRAY['people']; candidate_scopes:=ARRAY[global_scope];
            ELSIF TG_TABLE_NAME IN ('canonical_venues','venue_names','venue_locations','venue_external_identities',
                    'venue_redirects','imported_venue_assignments','show_venue_assignments','venue_assignment_evidence') THEN
                candidate_domains:=ARRAY['venues']; candidate_scopes:=ARRAY[global_scope];
            ELSIF TG_TABLE_NAME IN ('compositions','work_credits') THEN
                candidate_domains:=ARRAY['compositions']; candidate_scopes:=ARRAY[global_scope];
            ELSE
                -- These are current accepted music projections. Immutable revision/evidence
                -- history is not candidate state: the current FK revision supplies its evidence
                -- JSON. Setlists, file contents and their history are deliberately not read here.
                candidate_domains:=ARRAY['compositions']; candidate_scopes:=ARRAY[global_scope];
                FOREACH row_value IN ARRAY ARRAY[old_row,new_row] LOOP
                    IF row_value='{}'::jsonb THEN CONTINUE; END IF;
                    IF TG_TABLE_NAME='repertoire_songs' THEN
                        artist_keys:=array_append(artist_keys,(row_value->>'artist_uuid')::uuid);
                    ELSIF TG_TABLE_NAME IN ('imported_song_assignments','repertoire_compositions','music_external_identities') THEN
                        song_keys:=array_append(song_keys,(row_value->>'repertoire_song_uuid')::uuid);
                        IF TG_TABLE_NAME='music_external_identities' THEN
                            artist_keys:=array_append(artist_keys,(row_value->>'artist_uuid')::uuid);
                        END IF;
                    END IF;
                END LOOP;
                IF cardinality(song_keys)>0 THEN
                    SELECT artist_keys||COALESCE(array_agg(artist_uuid),'{}') INTO artist_keys
                        FROM normalization.repertoire_songs WHERE uuid=ANY(song_keys);
                END IF;
                FOR artist_key IN SELECT DISTINCT key FROM unnest(artist_keys) key WHERE key IS NOT NULL ORDER BY key LOOP
                    candidate_domains:=array_append(candidate_domains,'repertoire');
                    candidate_scopes:=array_append(candidate_scopes,artist_key);
                END LOOP;
            END IF;
            PERFORM normalization.bump_candidate_universes(candidate_domains,candidate_scopes);
            RETURN NULL;
        END $$;

        DO $$ DECLARE relation text; BEGIN
            FOREACH relation IN ARRAY ARRAY['people','person_aliases','person_external_identities','person_redirects',
                'recording_credits','recording_credit_evidence','canonical_venues','venue_names','venue_locations',
                'venue_external_identities','venue_redirects','imported_venue_assignments','show_venue_assignments',
                'venue_assignment_evidence','repertoire_songs','imported_song_assignments','repertoire_compositions',
                'music_external_identities','compositions','work_credits'] LOOP
                EXECUTE format('CREATE TRIGGER normalization_candidate_matching_revision AFTER INSERT OR UPDATE OR DELETE ON normalization.%I FOR EACH ROW EXECUTE FUNCTION normalization.record_candidate_matching_revision()',relation);
            END LOOP;
        END $$;

        CREATE FUNCTION normalization.record_candidate_person_context() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF TG_TABLE_NAME='sources' THEN
                    IF OLD.uuid IS NOT DISTINCT FROM NEW.uuid AND OLD.artist_id IS NOT DISTINCT FROM NEW.artist_id THEN
                        RETURN NULL;
                    END IF;
                ELSIF OLD.uuid IS NOT DISTINCT FROM NEW.uuid THEN
                    RETURN NULL;
                END IF;
            END IF;
            -- Unconditional context invalidation covers a source arriving for a previously
            -- unbound credit. An existence precheck could race that credit's publication.
            PERFORM normalization.bump_candidate_universes(ARRAY['people'],
                ARRAY['00000000-0000-0000-0000-000000000000'::uuid]);
            RETURN NULL;
        END $$;
        -- Follow catalog-clock callbacks on these public tables.
        CREATE TRIGGER normalization_universe_context_revision AFTER INSERT OR UPDATE OR DELETE ON sources
            FOR EACH ROW EXECUTE FUNCTION normalization.record_candidate_person_context();
        CREATE TRIGGER normalization_universe_context_revision AFTER INSERT OR UPDATE OR DELETE ON artists
            FOR EACH ROW EXECUTE FUNCTION normalization.record_candidate_person_context();

        CREATE FUNCTION normalization.fence_artist_candidate_delete() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- Migration 24's catalog and migration 29's imported-venue BEFORE callbacks have
            -- already locked earlier clocks. Cascading provider mappings can acquire this
            -- registry before sources reach their context callback, so prelock both now.
            PERFORM generation FROM normalization.catalog_mapping_registry_revision WHERE singleton FOR UPDATE;
            PERFORM generation FROM normalization.candidate_universe_revisions
                WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'::uuid FOR UPDATE;
            RETURN OLD;
        END $$;
        CREATE TRIGGER normalization_universe_artist_delete BEFORE DELETE ON artists
            FOR EACH ROW EXECUTE FUNCTION normalization.fence_artist_candidate_delete();

        CREATE FUNCTION normalization.record_candidate_universe_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE candidate_domains text[]; candidate_scopes uuid[];
        BEGIN
            SELECT array_agg(domain ORDER BY domain COLLATE "C",scope_uuid),
                array_agg(scope_uuid ORDER BY domain COLLATE "C",scope_uuid) INTO candidate_domains,candidate_scopes
                FROM normalization.candidate_universe_revisions;
            PERFORM normalization.bump_candidate_universes(candidate_domains,candidate_scopes);
            RETURN NULL;
        END $$;
        -- Offline TRUNCATE can remove FK children too. Advancing every retained scope is
        -- conservative and preserves negative/positive observations without inspecting
        -- already removed rows. Pause writers/publications before relation locks are taken.
        DO $$ DECLARE relation text; BEGIN
            FOREACH relation IN ARRAY ARRAY['public.artists','public.sources',
                'normalization.people','normalization.person_aliases','normalization.person_external_identities','normalization.person_redirects',
                'normalization.recording_credits','normalization.recording_credit_evidence',
                'normalization.canonical_venues','normalization.venue_names','normalization.venue_locations','normalization.venue_external_identities',
                'normalization.venue_redirects','normalization.imported_venue_assignments','normalization.show_venue_assignments','normalization.venue_assignment_evidence',
                'normalization.repertoire_songs','normalization.imported_song_assignments','normalization.repertoire_compositions',
                'normalization.music_external_identities','normalization.compositions','normalization.work_credits'] LOOP
                EXECUTE format('CREATE TRIGGER normalization_universe_truncate AFTER TRUNCATE ON %s FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_candidate_universe_truncate()',relation);
            END LOOP;
        END $$;
        -- Normalization publications/reviews declare and lock their complete candidate write
        -- scopes before resources and owners. Accepted-table SQL maintenance must prelock all
        -- affected catalog/universe scopes in that same order, or stop workers first. These
        -- callbacks provide transactional invalidation, not a direct-SQL admission protocol.
        -- Reviewed exclusions remain enforced under owner editorial dependencies; they are not
        -- projected into candidate facts. Appending immutable music history alone changes no candidate.
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization publication on rollback; retain candidate universes and contribution ownership."
        );
}
