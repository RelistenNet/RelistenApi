using SimpleMigrations;

namespace Migrations
{
    [Migration(7, "Fixes unique indexes blocking phish.in imports.")]
    public class FixUniqueIndexes : Migration
    {
        protected override void Up()
        {
            // In practice, there won't be duplicates but we don't need to enforce at the DB level
            Execute(@"
                ALTER TABLE source_tracks DROP CONSTRAINT IF EXISTS source_tracks_source_id_slug_key;
                DROP INDEX IF EXISTS source_tracks_source_id_slug_key;

                -- every other table has upstream_identifier scoped to the artist. not sure what happened here
                ALTER TABLE setlist_shows DROP CONSTRAINT IF EXISTS setlist_show_upstream_identifier_key;
                ALTER TABLE setlist_shows ADD CONSTRAINT setlist_show_upstream_identifier_key UNIQUE (artist_id, upstream_identifier);
            ");
        }

        protected override void Down()
        {
            throw new System.NotImplementedException();
        }
    }
}
