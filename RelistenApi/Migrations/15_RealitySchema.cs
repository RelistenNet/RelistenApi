using SimpleMigrations;

namespace Migrations;

[Migration(15, "Create the reality schema (versions, builds, segments, version maps)")]
public sealed class RealitySchema : SqlFileMigration
{
    protected override void Up() => ExecuteSqlFile("015_reality.sql");
}
