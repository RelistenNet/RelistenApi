using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;

namespace RelistenIngestionWorker;

/// <summary>Configures worker diagnostics independently of the API's HTTP host.</summary>
public static class WorkerLogging
{
    /// <summary>
    /// Keeps activity scopes in console logs and registers Sentry when SENTRY_DSN is set.
    /// </summary>
    /// <returns>
    /// SDK options, or null when reporting is disabled. The caller must initialize
    /// the SDK before building the host and dispose it after the host shuts down.
    /// </returns>
    public static SentryOptions? Configure(HostApplicationBuilder builder)
    {
        // JSON preserves dictionary scope fields such as artist and workflow ID.
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

        var sentryDsn = builder.Configuration["SENTRY_DSN"];
        if (string.IsNullOrWhiteSpace(sentryDsn))
        {
            return null;
        }

        // Sentry 6's logging provider does not dispose its global SDK. The entrypoint
        // owns that lifetime so operator commands and normal shutdown flush events.
        builder.Logging.AddSentry(options => options.InitializeSdk = false);

        return new SentryOptions
        {
            Dsn = sentryDsn,
            Environment = builder.Environment.EnvironmentName,
            SendDefaultPii = false,
        };
    }
}
