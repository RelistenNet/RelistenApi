using SimpleMigrations;

namespace Migrations;

[Migration(23, "Track artist search reindex requests across catalog builds")]
public sealed class SearchArtistReindexes : SqlFileMigration
{
    protected override void Up() =>
        Execute(
            """
            CREATE TABLE search_artist_reindexes (
                artist_id integer PRIMARY KEY REFERENCES artists(id) ON DELETE CASCADE,
                requested_at timestamptz NOT NULL
            )
            """
        );

    protected override void Down() => Execute("DROP TABLE search_artist_reindexes");
}
