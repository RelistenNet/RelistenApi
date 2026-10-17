using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.TapersSection;

namespace RelistenApiTests.TapersSection;

[TestFixture]
public class TestTapersSectionSearchClient
{
    [Test]
    public async Task ConfiguredAadamCollectionReadsItsOwnItems()
    {
        using var handler = new SearchHandler(
            """
            {"items":[{"identifier":"aadam-recording","collection":["aadamjacobs","audio"],"mediatype":"audio"}],"total":1}
            """
        );
        using var http = new HttpClient(handler);

        var page = await new ArchiveCollectionSearchClient(http, "aadamjacobs").FetchPageAsync();

        page.Items.Select(item => item.Value).Should().Equal("aadam-recording");
        page.Cursor.Should().BeNull();
        QueryHelpers
            .ParseQuery(handler.Requests.Single().Query)["q"]
            .ToString()
            .Should()
            .Be("collection:aadamjacobs AND mediatype:audio");
    }

    [Test]
    public async Task AConfiguredCollectionCannotAcceptAnotherCollectionsItems()
    {
        using var handler = new SearchHandler(Page(1, 1, null));
        using var http = new HttpClient(handler);

        var act = () => new ArchiveCollectionSearchClient(http, "aadamjacobs").FetchPageAsync();

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task ReadsLargeContinuationWithoutCountAndAcceptsDecreasingTotals()
    {
        using var handler = new SearchHandler(Page(100, 30180, "A"), Page(5000, 30080, "B"));
        using var http = new HttpClient(handler);
        var client = new ArchiveCollectionSearchClient(http, "taperssection");
        var first = await client.FetchPageAsync();
        var next = await client.FetchPageAsync(first.Cursor);
        first.Items.Should().HaveCount(100);
        next.Items.Should().HaveCount(5000);
        next.Cursor.Should().Be("B");
        var firstQuery = QueryHelpers.ParseQuery(handler.Requests[0].Query);
        firstQuery["q"].ToString().Should().Be("collection:taperssection AND mediatype:audio");
        firstQuery["count"].ToString().Should().Be("100");
        firstQuery["fields"].ToString().Should().Be("identifier,collection,mediatype");
        var nextQuery = QueryHelpers.ParseQuery(handler.Requests[1].Query);
        nextQuery["cursor"].ToString().Should().Be("A");
        nextQuery.Should().NotContainKey("count");
    }

    [Test]
    public async Task EmptyAdvancingPageIsNotTerminalAndOverlappingIdentifiersAreDeduplicated()
    {
        using var handler = new SearchHandler(
            Page(0, 20, "A"),
            """
            {
              "items": [
                {"identifier":"one","collection":"taperssection","mediatype":"audio"},
                {"identifier":"one","collection":["taperssection"],"mediatype":"audio"},
                {"identifier":"two","collection":["audio_music","taperssection"],"mediatype":"audio"}
              ],
              "total": 3
            }
            """
        );
        using var http = new HttpClient(handler);
        var client = new ArchiveCollectionSearchClient(http, "taperssection");
        var first = await client.FetchPageAsync();
        first.Items.Should().BeEmpty();
        first.Cursor.Should().Be("A");
        var terminal = await client.FetchPageAsync(first.Cursor);
        terminal.Items.Select(value => value.Value).Should().Equal("one", "two");
        terminal.Cursor.Should().BeNull();
    }

    [TestCase(
        "{\"items\":[{\"identifier\":\"one\",\"collection\":\"taperssection\",\"mediatype\":\"audio\"}],\"total\":5,\"cursor\":\"A\"}"
    )]
    [TestCase(
        "{\"items\":[{\"identifier\":\"one\",\"collection\":\"taperssection\",\"mediatype\":\"audio\"}],\"total\":5}"
    )]
    [TestCase("{\"items\":[{\"identifier\":\"\"}],\"total\":1}")]
    [TestCase("{\"error\":\"expired cursor\"}")]
    [TestCase("{\"items\":[],\"total\":0,\"cursor\":23}")]
    public async Task RejectsRepeatedCursorsIncompleteTerminalPagesAndInvalidResponses(string body)
    {
        using var handler = new SearchHandler(body);
        using var http = new HttpClient(handler);
        var act = () =>
            new ArchiveCollectionSearchClient(http, "taperssection").FetchPageAsync("A");
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [TestCase("{\"identifier\":\"unrelated\",\"collection\":\"texts\",\"mediatype\":\"audio\"}")]
    [TestCase(
        "{\"identifier\":\"unrelated\",\"collection\":[\"taperssection\"],\"mediatype\":\"collection\"}"
    )]
    [TestCase("{\"identifier\":\"unrelated\"}")]
    [TestCase(
        "{\"identifier\":\"unrelated\",\"collection\":\"taperssection\",\"mediatype\":[\"audio\"]}"
    )]
    public async Task RejectsSuccessfulResponsesThatDoNotMatchTheDiscoveryQuery(string item)
    {
        using var handler = new SearchHandler("{\"items\":[" + item + "],\"total\":1}");
        using var http = new HttpClient(handler);
        var act = () => new ArchiveCollectionSearchClient(http, "taperssection").FetchPageAsync();

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task CursorExpiryPropagatesAsFailureForRestartFromRetainedIdentifiers()
    {
        using var handler = new SearchHandler("cursor expired")
        {
            StatusCode = HttpStatusCode.BadRequest,
        };
        using var http = new HttpClient(handler);
        var act = () =>
            new ArchiveCollectionSearchClient(http, "taperssection").FetchPageAsync("old");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task RejectsPageLargerThanObservedProtocolBound()
    {
        using var handler = new SearchHandler(Page(5001, 5001, "B"));
        using var http = new HttpClient(handler);
        var act = () =>
            new ArchiveCollectionSearchClient(http, "taperssection").FetchPageAsync("A");
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private static string Page(int count, int total, string? cursor) =>
        JsonConvert.SerializeObject(
            new
            {
                items = Enumerable
                    .Range(0, count)
                    .Select(index => new
                    {
                        identifier = $"recording-{index}",
                        collection = new[] { "taperssection", "audio_music" },
                        mediatype = "audio",
                    }),
                total,
                cursor,
            }
        );

    private sealed class SearchHandler(params string[] responses) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(StatusCode)
                {
                    Content = new StringContent(responses[Requests.Count - 1]),
                }
            );
        }
    }
}
