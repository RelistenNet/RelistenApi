using SimpleMigrations;

namespace Migrations;

[Migration(11, "Add track-to-song matching junction table and track type classification")]
public class AddTrackSongMatching : Migration
{
    protected override void Up()
    {
        Execute(@"
            -- Track type classification: song, banter, tuning, crowd, soundcheck, etc.
            ALTER TABLE source_tracks
                ADD COLUMN track_type TEXT NOT NULL DEFAULT 'song',
                ADD COLUMN matched_song_id INTEGER REFERENCES setlist_songs(id) ON DELETE SET NULL,
                ADD COLUMN match_confidence REAL,
                ADD COLUMN match_method TEXT;

            CREATE INDEX idx_source_tracks_matched_song ON source_tracks (matched_song_id)
                WHERE matched_song_id IS NOT NULL;
            CREATE INDEX idx_source_tracks_type ON source_tracks (track_type);

            -- Junction table for many-to-many track-to-song matching
            -- A single track can match multiple songs (e.g. medleys: 'Scarlet > Fire')
            -- A single song can be matched by many tracks
            CREATE TABLE source_track_songs (
                id              SERIAL PRIMARY KEY,
                source_track_id INTEGER NOT NULL REFERENCES source_tracks(id) ON DELETE CASCADE,
                setlist_song_id INTEGER NOT NULL REFERENCES setlist_songs(id) ON DELETE CASCADE,
                confidence      REAL NOT NULL DEFAULT 0.0,
                method          TEXT NOT NULL DEFAULT 'slug',
                position        SMALLINT NOT NULL DEFAULT 0,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

                UNIQUE(source_track_id, setlist_song_id)
            );

            CREATE INDEX idx_track_songs_track ON source_track_songs (source_track_id);
            CREATE INDEX idx_track_songs_song ON source_track_songs (setlist_song_id);
        ");
    }

    protected override void Down()
    {
        Execute(@"
            DROP TABLE IF EXISTS source_track_songs;

            DROP INDEX IF EXISTS idx_source_tracks_matched_song;
            DROP INDEX IF EXISTS idx_source_tracks_type;
            ALTER TABLE source_tracks
                DROP COLUMN IF EXISTS track_type,
                DROP COLUMN IF EXISTS matched_song_id,
                DROP COLUMN IF EXISTS match_confidence,
                DROP COLUMN IF EXISTS match_method;
        ");
    }
}
