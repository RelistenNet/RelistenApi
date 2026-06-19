using Npgsql;

namespace Relisten.UserApi.Services;

public sealed class UserApiDbService
{
    private readonly IConfiguration _configuration;

    public UserApiDbService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public NpgsqlConnection CreateConnection()
    {
        return new NpgsqlConnection(BuildConnectionString());
    }

    public bool HasConfiguredDatabase => !string.IsNullOrWhiteSpace(ConfiguredDatabaseUrl);

    private string? ConfiguredDatabaseUrl => FirstConfiguredValue(
        "PGBOUNCER_DATABASE_URL",
        "DATABASE_URL",
        "UserData:DatabaseUrl");

    private string BuildConnectionString()
    {
        var databaseUrl = ConfiguredDatabaseUrl;
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            throw new InvalidOperationException(
                "User API database access requires PGBOUNCER_DATABASE_URL, DATABASE_URL, or UserData:DatabaseUrl.");
        }

        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        {
            return databaseUrl;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2)
        {
            throw new InvalidOperationException("Database URL must include username and password.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = Uri.UnescapeDataString(userInfo[1]),
            Database = uri.AbsolutePath.TrimStart('/'),
            IncludeErrorDetail = true,
            MaxAutoPrepare = 100,
            AutoPrepareMinUsages = 2
        };

        return builder.ConnectionString;
    }

    private string? FirstConfiguredValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = _configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
