using System;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Relisten.Ingestion;

/// <summary>Creates activity-scoped HTTP clients with cancellation, timeouts, and optional fixture routing.</summary>
public sealed class IngestionHttpClientFactory(IConfiguration configuration, ILoggerFactory logs)
{
    public HttpClient Create(CancellationToken cancellationToken)
    {
        var endpoint = configuration["Ingestion:UpstreamBaseUrl"];
        return new HttpClient(
            new IngestionHttpHandler(
                cancellationToken,
                endpoint == null ? null : new Uri(endpoint),
                logs.CreateLogger<IngestionHttpHandler>()
            )
        )
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }
}
