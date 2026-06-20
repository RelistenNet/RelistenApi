using Dapper;
using Relisten.UserApi.Migrations;

namespace Relisten.UserApi.Services;

public sealed class UserDataSchemaInitializer
{
    private readonly IConfiguration _configuration;
    private readonly UserApiDbService _db;

    public UserDataSchemaInitializer(IConfiguration configuration, UserApiDbService db)
    {
        _configuration = configuration;
        _db = db;
    }

    public async Task Initialize()
    {
        if (!_configuration.GetValue("UserData:InitializeSchema", true) || !_db.HasConfiguredDatabase)
        {
            return;
        }

        await using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(UserDataSchemaSql.Bootstrap);
        await connection.ExecuteAsync(UserDataSchemaSql.AuthTables);
        await connection.ExecuteAsync(UserDataSchemaSql.PlaylistTables);
        await connection.ExecuteAsync(UserDataSchemaSql.PlaylistSharingTables);
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.user_service_migrations (version, description)
            VALUES
                (1, 'Create user_data schema for Relisten user API'),
                (2, 'Create auth and session tables for Relisten user API'),
                (3, 'Create playlist tables for Relisten user API'),
                (4, 'Create playlist sharing tables for Relisten user API')
            ON CONFLICT (version) DO NOTHING;
            """);
    }
}
