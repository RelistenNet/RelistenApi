using SimpleMigrations;

namespace Migrations;

[Migration(26, "Publish recording and song performance popularity")]
public sealed class CatalogPerformancePopularity : SqlFileMigration
{
    protected override void Up() =>
        Execute(
            """
            CREATE TABLE recording_popularity (
                LIKE artist_popularity INCLUDING DEFAULTS,
                artist_uuid uuid NOT NULL REFERENCES artists(uuid) ON DELETE CASCADE,
                PRIMARY KEY (uuid),
                FOREIGN KEY (uuid) REFERENCES sources(uuid) ON DELETE CASCADE
            );
            CREATE INDEX recording_popularity_artist_idx ON recording_popularity (artist_uuid);

            CREATE TABLE song_performance_popularity (
                LIKE artist_popularity INCLUDING DEFAULTS,
                artist_uuid uuid NOT NULL REFERENCES artists(uuid) ON DELETE CASCADE,
                song_id text NOT NULL,
                PRIMARY KEY (uuid, song_id),
                FOREIGN KEY (uuid) REFERENCES shows(uuid) ON DELETE CASCADE
            );
            CREATE INDEX song_performance_popularity_artist_idx ON song_performance_popularity (artist_uuid);
            CREATE INDEX song_performance_popularity_song_idx ON song_performance_popularity (song_id, uuid);
            CREATE TABLE song_popularity (
                LIKE artist_popularity INCLUDING DEFAULTS,
                artist_uuid uuid NOT NULL REFERENCES artists(uuid) ON DELETE CASCADE,
                song_id text PRIMARY KEY
            );
            ALTER TABLE song_popularity DROP COLUMN uuid;
            CREATE INDEX song_popularity_artist_idx ON song_popularity (artist_uuid);

            CREATE TABLE popularity_detail_publications (
                artist_uuid uuid PRIMARY KEY REFERENCES artists(uuid) ON DELETE CASCADE,
                generated_at timestamptz NOT NULL
            );
            """
        );

    protected override void Down() =>
        Execute(
            "DROP TABLE popularity_detail_publications, song_popularity, song_performance_popularity, recording_popularity"
        );
}
