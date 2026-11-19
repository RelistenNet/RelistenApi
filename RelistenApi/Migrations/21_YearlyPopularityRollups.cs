using SimpleMigrations;

namespace Migrations;

[Migration(21, "Add monthly track and daily source play rollups")]
public sealed class YearlyPopularityRollups : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("021_yearly_popularity.sql");
}
