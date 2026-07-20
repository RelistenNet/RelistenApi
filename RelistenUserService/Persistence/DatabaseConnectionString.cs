using Npgsql;

namespace RelistenUserService.Persistence;

public static class DatabaseConnectionString
{
    private static readonly IReadOnlyDictionary<string, string> SupportedUriOptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application_name"] = "Application Name",
            ["channel_binding"] = "Channel Binding",
            ["check_certificate_revocation"] = "Check Certificate Revocation",
            ["connect_timeout"] = "Timeout",
            ["sslcert"] = "SSL Certificate",
            ["sslkey"] = "SSL Key",
            ["sslmode"] = "SSL Mode",
            ["sslpassword"] = "SSL Password",
            ["sslrootcert"] = "Root Certificate",
            ["target_session_attrs"] = "Target Session Attributes"
        };

    public static string Resolve(IConfiguration configuration)
    {
        // Deployments provide ACCOUNTS_DATABASE_URL as a PostgreSQL URI. It must
        // outrank file-based defaults so a local setting can never shadow the
        // production Secret mounted by the deployment manifest.
        var configured = configuration["ACCOUNTS_DATABASE_URL"]
            ?? configuration.GetConnectionString("Accounts")
            ?? configuration["DATABASE_URL"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Accounts or ACCOUNTS_DATABASE_URL is required.");
        }

        return Normalize(configured);
    }

    public static string ResolveRefreshTokenLocks(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration.GetConnectionString("AccountsLock")
            ?? configuration["ACCOUNTS_LOCK_DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (environment.IsDevelopment())
            {
                return Resolve(configuration);
            }

            throw new InvalidOperationException(
                "ConnectionStrings:AccountsLock or ACCOUNTS_LOCK_DATABASE_URL must point directly to the PostgreSQL primary.");
        }

        return Normalize(configured);
    }

    private static string Normalize(string configured) =>
        configured.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            || configured.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            ? ConvertUri(configured)
            : configured;

    private static string ConvertUri(string value)
    {
        var uri = new Uri(value);
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "PostgreSQL connection URI fragments are not supported.");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2)
        {
            throw new InvalidOperationException("The PostgreSQL URI must include a username and password.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = Uri.UnescapeDataString(userInfo[1]),
            IncludeErrorDetail = false
        };
        ApplyQueryOptions(builder, uri.Query);
        return builder.ConnectionString;
    }

    private static void ApplyQueryOptions(
        NpgsqlConnectionStringBuilder builder,
        string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query[1..].Split('&'))
        {
            var pair = segment.Split('=', 2);
            var option = Uri.UnescapeDataString(pair[0]);
            if (pair.Length != 2
                || string.IsNullOrWhiteSpace(option)
                || !SupportedUriOptions.TryGetValue(option, out var connectionStringKey))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL connection URI option '{option}' is not supported. " +
                    "Use an Npgsql key/value connection string for additional options.");
            }

            if (!seen.Add(option))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL connection URI option '{option}' is specified more than once.");
            }

            var optionValue = NormalizeOptionValue(
                option,
                Uri.UnescapeDataString(pair[1]));
            try
            {
                builder[connectionStringKey] = optionValue;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL connection URI option '{option}' has an invalid value.",
                    exception);
            }
        }
    }

    private static string NormalizeOptionValue(string option, string value) =>
        option.Equals("sslmode", StringComparison.OrdinalIgnoreCase)
        || option.Equals("channel_binding", StringComparison.OrdinalIgnoreCase)
            ? value.Replace("-", "", StringComparison.Ordinal)
                .Replace("_", "", StringComparison.Ordinal)
            : value;
}
