using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(7, "Add playlist sync metadata for Relisten user API")]
public sealed class AddPlaylistSyncMetadata : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.PlaylistSyncTables);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Playlist sync metadata changes are not reversible in automated migrations.");
    }
}
