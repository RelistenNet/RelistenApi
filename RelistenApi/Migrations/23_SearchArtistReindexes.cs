using SimpleMigrations;

namespace Migrations;

[Migration(23, "Track artist and playlist reindex requests across search builds")]
public sealed class SearchArtistReindexes : SqlFileMigration
{
    protected override void Up() =>
        Execute(
            """
            CREATE TABLE search_artist_reindexes (
                artist_id integer PRIMARY KEY REFERENCES artists(id) ON DELETE CASCADE,
                requested_at timestamptz NOT NULL
            );
            CREATE TABLE search_playlist_reindexes (
                playlist_id text PRIMARY KEY,
                requested_at timestamptz NOT NULL
            );
            """
        );

    protected override void Down() =>
        Execute("DROP TABLE search_playlist_reindexes; DROP TABLE search_artist_reindexes");
}
