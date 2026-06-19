using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(2, "Create auth and session tables for Relisten user API")]
public sealed class CreateAuthTables : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.AuthTables);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Auth tables are not reversible in automated migrations.");
    }
}
