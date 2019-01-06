using SimpleMigrations;

namespace Migrations
{
    [Migration(5, "Add source track plays index")]
    public class AddSourceTrackPlaysIndex : Migration
    {
        protected override void Up()
        {
            Execute(@"
                CREATE INDEX IF NOT EXISTS idx_source_track_plays_id_btree ON source_track_plays(id);
                CREATE INDEX CONCURRENTLY ON setlist_shows (artist_id);
            ");
        }

        protected override void Down()
        {
            throw new System.NotImplementedException();
        }
    }
}