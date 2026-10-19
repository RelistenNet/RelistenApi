using SimpleMigrations;

namespace Migrations;

[Migration(
    13,
    "Create the kb song catalogue: songs, aliases, evidence, track songs, runs, change log"
)]
public sealed class KbSchema : SqlFileMigration
{
    protected override void Up()
    {
        ExecuteSqlFile("013_kb.sql");
    }
}
