using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(3, "Create playlist tables for Relisten user API")]
public sealed class CreatePlaylistTables : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.PlaylistTables);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Playlist tables are not reversible in automated migrations.");
    }
}
