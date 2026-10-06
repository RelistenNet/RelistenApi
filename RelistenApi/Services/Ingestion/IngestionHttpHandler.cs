using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

// One activity attempt corresponds to at most one request, so native queue limits
// remain useful. The optional endpoint override is for isolated fixture servers.
internal sealed class IngestionHttpHandler(CancellationToken activityCancellation, Uri? endpoint)
    : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    private int requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (Interlocked.Increment(ref requests) != 1)
            throw new ApplicationFailureException(
                "An ingestion fetch activity attempted multiple HTTP requests.",
                nonRetryable: true
            );
        if (endpoint != null)
            request.RequestUri = new Uri(endpoint, request.RequestUri!.PathAndQuery);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            activityCancellation
        );
        var response = await base.SendAsync(request, linked.Token);
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
