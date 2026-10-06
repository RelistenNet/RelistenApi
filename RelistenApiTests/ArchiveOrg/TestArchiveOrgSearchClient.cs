using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgSearchClient
{
    [Test]
    public async Task FetchesBeyondTenThousandBeforeReconcilingSources()
    {
        using var handler = new SearchHandler(Response(10000, 18385), Response(18385, 18385));
        using var http = new HttpClient(handler);

        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        docs.Should().HaveCount(18385);
        handler.Requests.Select(Rows).Should().Equal(10000, 18385);
        foreach (var uri in handler.Requests)
        {
            uri.Scheme.Should().Be("https");
            var query = QueryHelpers.ParseQuery(uri.Query);
            query["q"].ToString().Should().Be("collection:GratefulDead");
            query.Keys.Should().NotContain(key => key.StartsWith("page") || key.StartsWith("sort"));
            query["fl[]"].Should().Contain(["identifier", "date", "addeddate", "reviewdate", "updatedate"]);
        }

        // The old first-page comparison falsely deleted these stored identifiers beyond row 10,000.
        var existing = Enumerable.Range(0, 18200).Select(Identifier).Append("actually-removed");
        var deleted = ArchiveOrgImporter.SourceIdentifiersToDelete(existing, docs, []);
        deleted.Should().Equal("actually-removed");
        ArchiveOrgImporter.ExceedsDeletionLimit(deleted.Count).Should().BeFalse();
    }

    [Test]
    public async Task ExpandsAgainWhenTheCollectionGrowsBetweenRequests()
    {
        using var handler = new SearchHandler(
            Response(10000, 10001), Response(10001, 10002), Response(10002, 10002));
        using var http = new HttpClient(handler);

        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        docs.Should().HaveCount(10002);
        handler.Requests.Select(Rows).Should().Equal(10000, 10001, 10002);
    }

    [Test]
    public async Task AcceptsACompleteResponseWhenTheCollectionShrinks()
    {
        using var handler = new SearchHandler(Response(10000, 10002), Response(10001, 10001));
        using var http = new HttpClient(handler);

        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        docs.Should().HaveCount(10001);
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task FailsWhenTheServiceKeepsCappingResultsInsteadOfReturningTheRequestedTotal()
    {
        using var handler = new SearchHandler(Response(10000, 18385), Response(10000, 18385));
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*incomplete*10000*18385*");
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task BoundsRetriesWhenTheCollectionNeverFitsTheRequestedCount()
    {
        using var handler = new SearchHandler(
            Response(10000, 10001), Response(10001, 10002), Response(10002, 10003));
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*after 3 requests*");
        handler.Requests.Should().HaveCount(3);
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
    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("{\"responseHeader\":{\"status\":0},\"response\":{\"numFound\":1,\"start\":0,\"docs\":[]}}")]
    public async Task ImportFailsBeforeAccessingLocalStateOnErrorOrIncompleteSearch(string body)
    {
        using var handler = new SearchHandler(body);
        using var http = new HttpClient(handler);
        // No database or source services: reaching preload/reconciliation would fail this test.
        using var importer = new SearchOnlyImporter(http);

        var act = () => importer.ImportDataForArtist(new Artist { slug = "grateful-dead" },
            new ArtistUpstreamSource { upstream_identifier = "GratefulDead" }, null);

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
    [TestCase("{\"responseHeader\":{},\"response\":{\"numFound\":0,\"start\":0,\"docs\":[]}}")]
    [TestCase("{\"responseHeader\":{\"status\":0},\"response\":{\"start\":0,\"docs\":[]}}")]
    [TestCase("{\"responseHeader\":{\"status\":0},\"response\":{\"numFound\":0,\"docs\":[]}}")]
    public async Task RejectsMalformedResponsesOrMissingRequiredCounts(string body)
    {
        using var handler = new SearchHandler(body);
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<JsonException>();
    }

    [TestCase("responseHeader.status", "1")]
    [TestCase("response.start", "1")]
    [TestCase("response.numFound", "-1")]
    [TestCase("response.docs", "null")]
    [TestCase("response.docs", "[null]")]
    [TestCase("response.docs", "[{\"identifier\":\"same\"},{\"identifier\":\"same\"}]")]
    [TestCase("response.docs", "[{\"identifier\":\"\"},{\"identifier\":\"valid\"}]")]
    [TestCase("response.docs", "[{}, {\"identifier\":\"valid\"}]")]
    public async Task RejectsInvalidCountsAndIdentifiers(string path, string value)
    {
        var body = JObject.Parse(Response(2, 2));
        body.SelectToken(path)!.Replace(JToken.Parse(value));
        using var handler = new SearchHandler(body.ToString());
        using var http = new HttpClient(handler);

        var act = () => new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task PreservesTolerantArchiveDateParsing()
    {
        var body = JObject.Parse(Response(1, 1));
        body["response"]!["docs"]![0]!["addeddate"] = "0000-01-01T00:00:00Z";
        body["response"]!["docs"]![0]!["date"] = "1966-XX-XX";
        using var handler = new SearchHandler(body.ToString());
        using var http = new HttpClient(handler);

        var docs = await new ArchiveOrgSearchClient(http).FetchAllAsync("GratefulDead", null);

        docs.Single().addeddate.Should().BeNull();
        docs.Single().raw_display_date.Should().Be("1966-XX-XX");
    }

    private static string Identifier(int index) => $"recording-{index:D5}";

    private static string Response(int count, int total) => JsonConvert.SerializeObject(new
    {
        responseHeader = new { status = 0 },
        response = new { numFound = total, start = 0,
            docs = Enumerable.Range(0, count).Select(index => new { identifier = Identifier(index) }) }
    });

    private static int Rows(Uri uri) => int.Parse(QueryHelpers.ParseQuery(uri.Query)["rows"].ToString());

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

    private sealed class SearchOnlyImporter : ArchiveOrgImporter
    {
        public SearchOnlyImporter(HttpClient client)
            : base(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        {
            http.Dispose();
            http = client;
        }
    }
}
