using System;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Relisten.Ingestion;

/// <summary>Creates activity-scoped HTTP clients with cancellation and timeouts over a pooled transport.</summary>
public sealed class IngestionHttpClientFactory(
    IHttpMessageHandlerFactory handlers,
    ILoggerFactory logs
)
{
    /// <summary>Creates a client owned by the calling activity and canceled with that activity.</summary>
    public HttpClient Create(CancellationToken cancellationToken)
    {
        var handler = new IngestionHttpHandler(
            cancellationToken,
            logs.CreateLogger<IngestionHttpHandler>()
        )
        {
            InnerHandler = handlers.CreateHandler(nameof(IngestionHttpClientFactory)),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }
}
