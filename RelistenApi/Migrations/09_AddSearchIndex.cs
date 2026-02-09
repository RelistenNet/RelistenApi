using SimpleMigrations;

namespace Migrations;

[Migration(9, "Add search_index table for hybrid full-text and semantic search")]
public class AddSearchIndex : Migration
{
    protected override void Up()
    {
        Execute(@"
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE EXTENSION IF NOT EXISTS pg_trgm;

            CREATE TABLE search_index (
                -- Identity (BIGINT to match sources.id and shows.id)
                source_id       BIGINT PRIMARY KEY REFERENCES sources(id) ON DELETE CASCADE,
                show_id         BIGINT NOT NULL REFERENCES shows(id) ON DELETE CASCADE,
                artist_id       INTEGER NOT NULL REFERENCES artists(id) ON DELETE CASCADE,

                -- Denormalized display fields (avoids joins at query time)
                artist_name     TEXT NOT NULL,
                show_date       DATE,
                show_year       SMALLINT,
                venue_name      TEXT,
                venue_location  TEXT,
                tour_name       TEXT,

                -- Source metadata
                is_soundboard   BOOLEAN DEFAULT FALSE,
                avg_rating      REAL,
                num_reviews     INTEGER DEFAULT 0,
                taper           TEXT,

                -- Search content
                track_titles    TEXT,
                -- search_text holds ONLY cleaned description + taper_notes + source + taper + transferrer + lineage + reviews
                -- (not artist/venue/tracks which have their own weighted tsvector entries)
                search_text     TEXT NOT NULL DEFAULT '',

                -- Semantic search (halfvec = half precision, 2 bytes/dim instead of 4)
                embedding       halfvec(1536),

                -- Keyword FTS with weighted fields
                -- A = high value entities, B = songs/tours, C = taper info, D = descriptions/notes/reviews
                search_tsv      tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('english', coalesce(artist_name, '')), 'A') ||
                    setweight(to_tsvector('english', coalesce(venue_name, '')), 'A') ||
                    setweight(to_tsvector('english', coalesce(venue_location, '')), 'A') ||
                    setweight(to_tsvector('english', coalesce(track_titles, '')), 'B') ||
                    setweight(to_tsvector('english', coalesce(tour_name, '')), 'B') ||
                    setweight(to_tsvector('english', coalesce(taper, '')), 'C') ||
                    setweight(to_tsvector('english', coalesce(search_text, '')), 'D')
                ) STORED,

                -- Housekeeping
                indexed_at      TIMESTAMPTZ DEFAULT NOW(),
                embedding_model TEXT DEFAULT 'text-embedding-3-small'
            );

            -- HNSW for vector similarity (halfvec, cosine distance)
            CREATE INDEX idx_search_embedding ON search_index
                USING hnsw (embedding halfvec_cosine_ops)
                WITH (m = 16, ef_construction = 128);

            -- GIN for full-text search
            CREATE INDEX idx_search_tsv ON search_index USING gin (search_tsv);

            -- Filtered lookups (used by PG18 iterative scan pre-filtering)
            CREATE INDEX idx_search_artist ON search_index (artist_id);
            CREATE INDEX idx_search_show ON search_index (show_id);
            CREATE INDEX idx_search_year ON search_index (show_year);
            CREATE INDEX idx_search_date ON search_index (show_date);
            CREATE INDEX idx_search_rating ON search_index (avg_rating DESC NULLS LAST);
            CREATE INDEX idx_search_soundboard ON search_index (is_soundboard) WHERE is_soundboard = TRUE;

            -- Extend source_reviews to support jam charts, HeadyVersion comments, etc.
            -- Existing rows are implicitly review_type='review', review_source='archive.org'
            ALTER TABLE source_reviews
                ADD COLUMN source_track_id BIGINT REFERENCES source_tracks(id) ON DELETE CASCADE,
                ADD COLUMN review_type TEXT NOT NULL DEFAULT 'review',
                ADD COLUMN review_source TEXT NOT NULL DEFAULT 'archive.org';
            -- review_type: 'review', 'jamchart', 'comment'
            -- review_source: 'archive.org', 'phish.net', 'headyversion'

            CREATE INDEX idx_source_reviews_track ON source_reviews (source_track_id)
                WHERE source_track_id IS NOT NULL;
            CREATE INDEX idx_source_reviews_type ON source_reviews (review_type);
            CREATE INDEX idx_source_reviews_source ON source_reviews (review_source);
        ");
    }

    protected override void Down()
    {
        Execute(@"
            DROP TABLE IF EXISTS search_index;

            DROP INDEX IF EXISTS idx_source_reviews_track;
            DROP INDEX IF EXISTS idx_source_reviews_type;
            DROP INDEX IF EXISTS idx_source_reviews_source;
            ALTER TABLE source_reviews
                DROP COLUMN IF EXISTS source_track_id,
                DROP COLUMN IF EXISTS review_type,
                DROP COLUMN IF EXISTS review_source;
        ");
    }
}
