using SimpleMigrations;

namespace Migrations;

[Migration(12, "Add canonical venues for cross-artist venue deduplication")]
public class AddCanonicalVenues : Migration
{
    protected override void Up()
    {
        Execute(@"
            -- Global canonical venues table (artist-independent)
            -- Groups artist-scoped venues that represent the same physical location.
            -- e.g. 'Fox Theatre, Atlanta, GA' groups 3 different artist-scoped Fox Theatre entries
            CREATE TABLE canonical_venues (
                id              SERIAL PRIMARY KEY,
                name            TEXT NOT NULL,
                location        TEXT NOT NULL,
                latitude        DOUBLE PRECISION,
                longitude       DOUBLE PRECISION,
                slug            TEXT NOT NULL,
                past_names      TEXT,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                uuid            UUID NOT NULL UNIQUE DEFAULT gen_random_uuid()
            );

            CREATE INDEX idx_canonical_venues_slug ON canonical_venues (slug);
            CREATE INDEX idx_canonical_venues_name ON canonical_venues USING gin (
                to_tsvector('simple', name)
            );

            -- Optional: geographic index for proximity-based dedup
            -- (requires PostGIS if we want true geo queries, but for now
            -- simple lat/lng comparison is sufficient)

            -- Link artist-scoped venues to their canonical venue
            ALTER TABLE venues
                ADD COLUMN canonical_venue_id INTEGER REFERENCES canonical_venues(id) ON DELETE SET NULL;

            CREATE INDEX idx_venues_canonical ON venues (canonical_venue_id)
                WHERE canonical_venue_id IS NOT NULL;
        ");
    }

    protected override void Down()
    {
        Execute(@"
            DROP INDEX IF EXISTS idx_venues_canonical;
            ALTER TABLE venues
                DROP COLUMN IF EXISTS canonical_venue_id;

            DROP TABLE IF EXISTS canonical_venues;
        ");
    }
}
