using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;

namespace Relisten.Normalization.Batch;

/// <summary>Creates the tool's dedicated local cache connection without starting an API host.</summary>
internal static class NormalizationLocalDatabase
{
    internal static DbService Create()
    {
        var databaseUrl = Environment.GetEnvironmentVariable("RELISTEN_NORMALIZATION_DATABASE_URL");
        if (
            !Uri.TryCreate(databaseUrl, UriKind.Absolute, out var database)
            || database.Scheme != "postgresql"
            || database.Host is not ("127.0.0.1" or "localhost")
            || !database.AbsolutePath.StartsWith(
                "/relisten_normalization_",
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "Set RELISTEN_NORMALIZATION_DATABASE_URL to a dedicated local relisten_normalization_* database. See the selected mode's schema requirements."
            );
        }

        return new DbService(
            databaseUrl!,
            null,
            null,
            Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
            ).Environment,
            NullLogger<DbService>.Instance
        );
    }
}
