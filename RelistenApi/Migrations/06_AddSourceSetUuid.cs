using SimpleMigrations;

namespace Migrations
{
    [Migration(6, "Adds UUID to source_sets")]
    public class AddSourceSetUuid : Migration
    {
        protected override void Up()
        {
            Execute(@"
                ALTER TABLE source_sets ADD COLUMN uuid uuid NULL UNIQUE;
                
                UPDATE source_sets
                SET uuid = md5(sources.uuid || '::source_set::' || source_sets.index)::uuid
                FROM sources
                WHERE sources.id = source_sets.source_id;

                ALTER TABLE source_sets ALTER COLUMN uuid SET NOT NULL;
            ");
        }

        protected override void Down()
        {
            throw new System.NotImplementedException();
        }
    }
}