using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(5, "Add playlist block foreign key for Relisten user API")]
public sealed class AddPlaylistBlockForeignKey : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.PlaylistBlockForeignKey);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Playlist block foreign key changes are not reversible in automated migrations.");
    }
}
