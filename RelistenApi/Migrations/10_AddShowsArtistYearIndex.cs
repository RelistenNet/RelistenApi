using SimpleMigrations;

namespace Migrations;

[Migration(10, "Add indexes for artist show queries")]
public class AddShowsArtistYearIndex : Migration
{
    public AddShowsArtistYearIndex()
    {
        this.UseTransaction = false;
    }

    protected override void Up()
    {
        Execute(@"
            CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_shows_artist_id_year_id ON shows (artist_id, year_id);
        ");
        Execute(@"
            CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_show_source_information_artist_id ON show_source_information (artist_id) INCLUDE (source_count);
        ");
    }

    protected override void Down()
    {
        throw new System.NotImplementedException();
    }
}
