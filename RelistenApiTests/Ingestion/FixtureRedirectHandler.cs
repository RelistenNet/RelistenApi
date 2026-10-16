namespace RelistenApiTests.Ingestion;

// Route provider URLs to the local fixture while preserving their paths, queries, and headers.
internal sealed class FixtureRedirectHandler(Uri endpoint)
    : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        request.RequestUri = new Uri(endpoint, request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}
