using SimpleMigrations;

namespace Migrations
{
    [Migration(3, "Add table to store track plays persistently")]
    public class TrackPlays : Migration
    {
        protected override void Up()
        {
            Execute(@"
CREATE TABLE source_track_plays (
    id SERIAL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    source_track_uuid uuid NULL,
    user_uuid uuid NULL,
    app_type int2 NOT NULL
);

CREATE INDEX idx_source_track_plays_created_at
    ON source_track_plays
    USING BRIN (created_at) WITH (pages_per_range = 128);

ALTER TABLE ONLY source_track_plays
    ADD CONSTRAINT source_track_plays_source_track_uuid_fkey FOREIGN KEY (source_track_uuid) REFERENCES source_tracks(uuid) ON DELETE SET NULL;
           ");
        }

        protected override void Down()
        {
            Execute(@"
DROP TABLE source_track_plays;
            ");
        }
    }
}