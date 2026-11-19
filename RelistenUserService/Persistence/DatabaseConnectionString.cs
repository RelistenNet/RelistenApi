using Npgsql;
using Relisten.Persistence;

namespace RelistenUserService.Persistence;

public static class DatabaseConnectionString
{
    public static string Resolve(IConfiguration configuration)
    {
        var direct = ResolvePrimary(configuration);
        var options = new NpgsqlConnectionStringBuilder(direct);
        if (!options.ShouldSerialize("Maximum Pool Size"))
        {
            options.MaxPoolSize = 100;
        }

        var poolerHost = configuration["PGBOUNCER_WRITE_HOST"];
        if (!string.IsNullOrWhiteSpace(poolerHost))
        {
            // Only runtime routing changes; migrations and refresh locks retain the direct primary.
            options.Host = poolerHost;
        }

        return options.ConnectionString;
    }

    public static string ResolveMigrations(IConfiguration configuration)
    {
        var configured = configuration["ACCOUNTS_MIGRATION_DATABASE_URL"];
        return string.IsNullOrWhiteSpace(configured)
            ? ResolvePrimary(configuration)
            : PostgresConnectionString.Normalize(configured);
    }

    private static string ResolvePrimary(IConfiguration configuration)
    {
        // Deployments provide ACCOUNTS_DATABASE_URL as a PostgreSQL URI. It must
        // outrank file-based defaults so a local setting can never shadow the
        // production Secret mounted by the deployment manifest.
        var configured =
            configuration["ACCOUNTS_DATABASE_URL"]
            ?? configuration.GetConnectionString("Accounts")
            ?? configuration["DATABASE_URL"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Accounts or ACCOUNTS_DATABASE_URL is required."
            );
        }

        return PostgresConnectionString.Normalize(configured);
    }

    public static string ResolveRefreshTokenLocks(
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        var configured =
            configuration["ACCOUNTS_LOCK_DATABASE_URL"]
            ?? configuration.GetConnectionString("AccountsLock");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (environment.IsDevelopment())
            {
                configured = ResolvePrimary(configuration);
            }
            else
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:AccountsLock or ACCOUNTS_LOCK_DATABASE_URL must point directly to the PostgreSQL primary."
                );
            }
        }

        var maximumPoolSize = configuration.GetValue<int>("ACCOUNTS_REFRESH_MAX_POOL_SIZE", 16);
        if (maximumPoolSize < 1)
        {
            throw new InvalidOperationException("ACCOUNTS_REFRESH_MAX_POOL_SIZE must be positive.");
        }

        return new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(configured))
        {
            ApplicationName = "Relisten.UserService.RefreshLocks",
            CommandTimeout = 10,
            Enlist = false,
            MaxPoolSize = maximumPoolSize,
            MinPoolSize = 0,
            Pooling = true,
        }.ConnectionString;
    }
}
