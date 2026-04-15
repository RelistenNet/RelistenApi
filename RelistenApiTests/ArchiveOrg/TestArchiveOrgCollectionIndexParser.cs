using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Vendor.ArchiveOrg;

namespace RelistenApiTests.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgCollectionIndexParser
{
    [Test]
    public void Parse_ShouldReadCollectionItems()
    {
        var json = TestUtils.ReadFixture("ArchiveOrg/collection-index.json");
        var parsed = ArchiveOrgCollectionIndexParser.Parse(json);

        parsed.items.Should().NotBeNull();
        parsed.items.Count.Should().Be(3);
        parsed.items[0].identifier.Should().Be("Guster");
        parsed.items[0].item_count.Should().Be(12);
    }

    [Test]
    public async Task FetchCollectionItemsAsync_ShouldFollowCursorWhenScrapeTotalIsRemainingCount()
    {
        var handler = new QueueHttpMessageHandler(
            """
            {
              "total": 5,
              "count": 2,
              "cursor": "cursor-one",
              "items": [{"identifier": "item-1"}, {"identifier": "item-2"}]
            }
            """,
            """
            {
              "total": 3,
              "count": 2,
              "cursor": "cursor-two",
              "items": [{"identifier": "item-3"}, {"identifier": "item-4"}]
            }
            """,
            """
            {
              "total": 1,
              "count": 1,
              "cursor": null,
              "items": [{"identifier": "item-5"}]
            }
            """);

        using var client = new ArchiveOrgCollectionIndexClient(new HttpClient(handler));

        var items = await client.FetchCollectionItemsAsync("aadamjacobs", 100, CancellationToken.None);

        items.Select(item => item.identifier).Should().Equal("item-1", "item-2", "item-3", "item-4", "item-5");
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Query.Should().NotContain("cursor=");
        handler.Requests[1].Query.Should().Contain("cursor=cursor-one");
        handler.Requests[2].Query.Should().Contain("cursor=cursor-two");
    }

    [Test]
    public async Task FetchCollectionItemsAsync_ShouldFailWhenCursorDoesNotAdvance()
    {
        var handler = new QueueHttpMessageHandler(
            """
            {
              "total": 5,
              "count": 2,
              "cursor": "cursor-one",
              "items": [{"identifier": "item-1"}, {"identifier": "item-2"}]
            }
            """,
            """
            {
              "total": 3,
              "count": 2,
              "cursor": "cursor-one",
              "items": [{"identifier": "item-3"}, {"identifier": "item-4"}]
            }
            """);

        using var client = new ArchiveOrgCollectionIndexClient(new HttpClient(handler));

        var action = () => client.FetchCollectionItemsAsync("aadamjacobs", 100, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("archive.org scraping cursor did not advance*");
        handler.Requests.Should().HaveCount(2);
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> responses;

        public QueueHttpMessageHandler(params string[] responses)
        {
            this.responses = new Queue<string>(responses);
        }

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue())
            });
        }
    }
}
