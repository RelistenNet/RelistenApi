using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(1, "Create user_data schema for Relisten user API")]
public sealed class CreateUserDataSchema : Migration
{
    protected override void Up()
    {
        Execute(UserDataSchemaSql.Bootstrap);
    }

    protected override void Down()
    {
        throw new NotSupportedException("The user_data schema bootstrap is intentionally not reversible.");
    }
}
