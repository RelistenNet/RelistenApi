using SimpleMigrations;

namespace Migrations;

[Migration(8, "Add unique index to source_track_plays_by_day_6mo")]
public class AddUniqueIndexToView: Migration {
    public AddUniqueIndexToView()
    {
        this.UseTransaction = false;
    }
    
    protected override void Up()
    {
        // In practice, there won't be duplicates but we don't need to enforce at the DB level
        Execute(@"
            CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ""source_track_plays_by_hour_48h_play_hour_source_track_uuid_idx"" ON ""public"".""source_track_plays_by_hour_48h""(""play_hour"",""source_track_uuid"");
            CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ""source_track_plays_by_day_6mo_source_track_uuid_play_day_idx"" ON ""source_track_plays_by_day_6mo""(""source_track_uuid"",""play_day"");
        ");
    }

    protected override void Down()
    {
        throw new System.NotImplementedException();
    }
}