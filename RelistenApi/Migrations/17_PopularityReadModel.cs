using SimpleMigrations;

namespace Migrations;

[Migration(17, "Publish derived popularity for indexed catalog reads")]
public sealed class PopularityReadModel : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("017_popularity.sql");
}
