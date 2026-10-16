using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

// GETs may repeat on activity retry. Queue limits control concurrent item operations.
internal sealed class IngestionHttpHandler(
    CancellationToken activityCancellation,
    ILogger<IngestionHttpHandler>? log = null
) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // Exclude query parameters: some provider URLs carry API credentials.
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        log?.LogInformation("Requesting {UpstreamUrl}", url);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            activityCancellation
        );
        var response = await base.SendAsync(request, linked.Token);
        log?.LogInformation(
            "Upstream returned HTTP {StatusCode} for {UpstreamUrl}",
            (int)response.StatusCode,
            url
        );
        if (
            response.StatusCode == HttpStatusCode.TooManyRequests
            || (int)response.StatusCode >= 500
        )
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ApplicationFailureException(
                $"Upstream returned HTTP {status}.",
                nextRetryDelay: delay > TimeSpan.Zero ? delay : null
            );
        }
        // Buffer while the activity cancellation token is still in scope.
        try
        {
            await response.Content.LoadIntoBufferAsync(64 * 1024 * 1024, linked.Token);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
