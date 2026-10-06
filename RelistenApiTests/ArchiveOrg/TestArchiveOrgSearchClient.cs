using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgSearchClient
{
    [Test]
    public async Task FetchesLargeCollectionInOneWildcardRequest()
    {
        using var handler = new SearchHandler(Response(35000, 35000));
        using var http = new HttpClient(handler);

        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        docs.Should().HaveCount(35000);
        docs.Last().identifier.Should().Be("recording-34999");
        var uri = handler.Requests.Single();
        uri.Scheme.Should().Be("https");
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["q"].ToString().Should().Be("collection:GratefulDead");
        query["rows"].ToString().Should().Be("*");
        query.Keys.Should().NotContain(key => key.StartsWith("page") || key.StartsWith("sort"));
    }

    [TestCase(null)]
    [TestCase(2026)]
    public async Task AcceptsAnExplicitlyEmptyResultAndPreservesTheYearFilter(int? year)
    {
        using var handler = new SearchHandler(Response(0, 0));
        using var http = new HttpClient(handler);

        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", year);

        docs.Should().BeEmpty();
        QueryHelpers.ParseQuery(handler.Requests.Single().Query)["q"].ToString()
            .Should().Be(year.HasValue ? "collection:GratefulDead AND year:2026" : "collection:GratefulDead");
    }

    [TestCase("{\"error\":\"[DEEP_PAGING] limit exceeded\"}")]
    [TestCase("{\"responseHeader\":{\"status\":0},\"response\":{\"numFound\":1,\"start\":0,\"docs\":[]}}")]
    [TestCase("{\"responseHeader\":{\"status\":0},\"response\":{\"numFound\":2,\"start\":0,\"docs\":[{\"identifier\":\"same\"},{\"identifier\":\"same\"}]}}")]
    public async Task RejectsApiErrorsTruncatedResponsesAndDuplicateIdentifiers(string body)
    {
        using var handler = new SearchHandler(body);
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.Requests.Should().HaveCount(1);
    }

    [Test]
    public async Task RejectsHttpFailures()
    {
        using var handler = new SearchHandler(Response(0, 0)) { StatusCode = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [TestCase("<html>upstream error</html>")]
    [TestCase("{\"responseHeader\":{\"status\":0},\"response\":{\"start\":0,\"docs\":[]}}")]
    public async Task RejectsMalformedResponsesOrMissingTotal(string body)
    {
        using var handler = new SearchHandler(body);
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<JsonException>();
    }

    private static string Response(int count, int total) => JsonConvert.SerializeObject(new
    {
        responseHeader = new { status = 0 },
        response = new { numFound = total, start = 0,
            docs = Enumerable.Range(0, count).Select(index => new { identifier = $"recording-{index:D5}" }) }
    });

    private sealed class SearchHandler(params string[] responses) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(responses[Requests.Count - 1])
            });
        }
    }
}
