using System.Reflection;
using FluentAssertions;
using Relisten.UserApi.Migrations;
using SimpleMigrations;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryMigrationTests
{
    [Test]
    public void BootstrapSql_ShouldCreateOnlyUserDataSchemaObjects()
    {
        UserDataSchemaSql.SchemaName.Should().Be("user_data");
        UserDataSchemaSql.MigrationTableName.Should().Be("user_service_migrations");
        UserDataSchemaSql.Bootstrap.Should().Contain("CREATE SCHEMA IF NOT EXISTS user_data");
        UserDataSchemaSql.Bootstrap.Should().Contain("user_data.user_service_migrations");
        UserDataSchemaSql.Bootstrap.Should().NotContain("public.");
    }

    [Test]
    public void CreateUserDataSchemaMigration_ShouldBeFirstUserApiMigration()
    {
        var migration = typeof(CreateUserDataSchema)
            .GetCustomAttribute<MigrationAttribute>();

        migration.Should().NotBeNull();
        migration!.Version.Should().Be(1);
    }
}
