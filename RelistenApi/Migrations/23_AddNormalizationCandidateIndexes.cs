using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Indexes accepted names for bounded candidate retrieval, not identity acceptance.</summary>
[Migration(23, "Index normalization candidates and music discovery relationships")]
public sealed class AddNormalizationCandidateIndexes : Migration
{
    public const string SchemaSql = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        -- Reads and candidate retrieval use the same Unicode and whitespace key. The key
        -- only supports discovery; it never establishes that two titles identify one work.
        CREATE FUNCTION normalization.music_name_key(value text) RETURNS text
        LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
            SELECT btrim(regexp_replace(lower(normalize(value,NFKC)), '[[:space:]]+', ' ', 'g'))
        $$;

        -- This function has text-only inputs. Unlike polymorphic array_to_string, its output
        -- does not depend on session formatting settings. Names remain intact in domain rows.
        CREATE FUNCTION normalization.music_search_names(title text, aliases text[]) RETURNS text
        LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
            SELECT normalization.music_name_key(title || ' ' || array_to_string(aliases,' '))
        $$;
        ALTER TABLE normalization.repertoire_songs ADD COLUMN candidate_names text
            GENERATED ALWAYS AS (normalization.music_search_names(display_title,aliases)) STORED;
        ALTER TABLE normalization.compositions ADD COLUMN candidate_names text
            GENERATED ALWAYS AS (normalization.music_search_names(display_title,aliases)) STORED;

        CREATE INDEX person_candidate_name ON normalization.people USING gist(name_key gist_trgm_ops);
        CREATE INDEX person_candidate_alias ON normalization.person_aliases USING gist(name_key gist_trgm_ops) WHERE state='accepted';
        CREATE INDEX venue_candidate_name ON normalization.canonical_venues USING gist(name_key gist_trgm_ops);
        CREATE INDEX venue_candidate_alias ON normalization.venue_names USING gist(name_key gist_trgm_ops) WHERE state='accepted';
        CREATE INDEX repertoire_candidate_name ON normalization.repertoire_songs USING gist(candidate_names gist_trgm_ops) WHERE NOT retracted;
        CREATE INDEX composition_candidate_name ON normalization.compositions USING gist(candidate_names gist_trgm_ops) WHERE NOT retracted;

        CREATE INDEX person_candidate_identity ON normalization.person_external_identities(lower(provider),resource_type,provider_key) WHERE state='accepted';
        CREATE INDEX venue_candidate_identity ON normalization.venue_external_identities(lower(provider),resource_type,provider_key) WHERE state='accepted';
        CREATE INDEX repertoire_candidate_identity ON normalization.music_external_identities(artist_uuid,lower(provider),resource_type,provider_key)
            WHERE NOT retracted AND repertoire_song_uuid IS NOT NULL;
        CREATE INDEX composition_candidate_identity ON normalization.music_external_identities(lower(provider),resource_type,provider_key)
            WHERE NOT retracted AND composition_uuid IS NOT NULL;

        CREATE INDEX occurrence_repertoire_discovery ON normalization.song_occurrences(repertoire_song_uuid,edition_uuid) WHERE NOT retracted;
        CREATE INDEX edition_show_discovery ON normalization.setlist_editions(show_uuid) WHERE NOT retracted;
        CREATE INDEX repertoire_composition_discovery ON normalization.repertoire_compositions(composition_uuid,repertoire_song_uuid) WHERE NOT retracted;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization workers and readers on rollback; retain identities and candidate indexes."
        );
}
