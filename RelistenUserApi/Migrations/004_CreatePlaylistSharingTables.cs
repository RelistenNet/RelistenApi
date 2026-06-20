using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(4, "Create playlist sharing tables for Relisten user API")]
public sealed class CreatePlaylistSharingTables : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.PlaylistSharingTables);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Playlist sharing tables are not reversible in automated migrations.");
    }
}
