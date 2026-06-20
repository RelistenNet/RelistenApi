using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(6, "Create favorites and settings tables for Relisten user API")]
public sealed class CreateFavoritesSettingsTables : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.FavoritesSettingsTables);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Favorites and settings tables are not reversible in automated migrations.");
    }
}
