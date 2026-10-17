using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Preserves canonical show identities and records accepted performance assignments.</summary>
[Migration(16, "Retain distinct show performances and assignment evidence")]
public sealed class AddShowPerformances : Migration
{
    public const string SchemaSql = """
        ALTER TABLE shows DROP CONSTRAINT IF EXISTS shows_artist_id_display_date_key;
        DROP INDEX IF EXISTS shows_artist_id_display_date_key;
        ALTER TABLE shows
            ADD COLUMN performance_key text NOT NULL DEFAULT 'legacy',
            ADD COLUMN performance_type text NOT NULL DEFAULT 'unknown'
                CHECK (performance_type IN ('unknown','afternoon','early','late','other')),
            ADD COLUMN performance_label text,
            ADD COLUMN is_legacy_survivor boolean NOT NULL DEFAULT true,
            ADD CONSTRAINT shows_artist_date_performance_key UNIQUE(artist_id,display_date,performance_key);
        CREATE UNIQUE INDEX shows_legacy_survivor ON shows(artist_id,display_date)
            WHERE is_legacy_survivor;
        CREATE TABLE show_performance_keys (
            artist_id integer NOT NULL REFERENCES artists(id),
            performance_key text NOT NULL,
            show_id bigint NOT NULL REFERENCES shows(id),
            PRIMARY KEY(artist_id,performance_key)
        );
        CREATE TABLE source_show_assignments (
            source_id bigint PRIMARY KEY REFERENCES sources(id) ON DELETE CASCADE,
            show_id bigint NOT NULL REFERENCES shows(id),
            assignment_kind text NOT NULL CHECK (assignment_kind IN ('accepted','legacy','unresolved','combined')),
            basis_display_date text NOT NULL,
            evidence jsonb NOT NULL DEFAULT '{}',
            updated_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX source_show_assignments_show ON source_show_assignments(show_id);
        INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
        SELECT src.id,sh.id,'legacy',sh.display_date,'{"basis":"existing published association"}'::jsonb
        FROM sources src JOIN shows sh ON sh.id=src.show_id AND sh.artist_id=src.artist_id;
        CREATE TABLE setlist_show_assignments (
            setlist_show_id bigint PRIMARY KEY REFERENCES setlist_shows(id) ON DELETE CASCADE,
            show_id bigint NOT NULL REFERENCES shows(id),
            evidence jsonb NOT NULL DEFAULT '{}',
            updated_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX setlist_show_assignments_show ON setlist_show_assignments(show_id);
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Performance splits cannot be collapsed without losing accepted identities. Pause old writers and retain this schema on rollback."
        );
}
