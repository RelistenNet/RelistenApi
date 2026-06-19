namespace Relisten.UserApi.Migrations;

public static class UserDataSchemaSql
{
    public const string SchemaName = "user_data";
    public const string MigrationTableName = "user_service_migrations";

    // Schema-qualify every bootstrap object so user-data migrations never depend on a default
    // search_path that might point at the catalog schema.
    public const string Bootstrap = """
        CREATE SCHEMA IF NOT EXISTS user_data;

        CREATE TABLE IF NOT EXISTS user_data.user_service_migrations (
            version BIGINT PRIMARY KEY,
            description TEXT NOT NULL,
            applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;
}
