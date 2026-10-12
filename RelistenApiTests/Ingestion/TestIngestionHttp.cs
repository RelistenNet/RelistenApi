using System.Net;
using FluentAssertions;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;
using Temporalio.Exceptions;

namespace RelistenApiTests.Ingestion;

[TestFixture]
public class TestIngestionHttp
{
    [Test]
    public async Task OneItemCanFollowARedirectAndFetchAnotherRelatedInput()
    {
        using var server = new FixtureHttpServer();
        server.Redirects["/first"] = "/canonical";
        using var http = new HttpClient(
            new IngestionHttpHandler(CancellationToken.None, server.Address)
        );
        using var first = await http.GetAsync("https://upstream.invalid/first");
        using var second = await http.GetAsync("https://upstream.invalid/reviews");
        first.IsSuccessStatusCode.Should().BeTrue();
        second.IsSuccessStatusCode.Should().BeTrue();
        server.Requests.Should().Be(3);
    }

    [Test]
    public async Task UpstreamRetryAfterControlsTheNextTemporalAttempt()
    {
        using var server = new FixtureHttpServer
        {
            Status = HttpStatusCode.TooManyRequests,
            RetryAfter = TimeSpan.FromSeconds(30),
        };
        using var http = new HttpClient(
            new IngestionHttpHandler(CancellationToken.None, server.Address)
        );
        Func<Task> fetch = async () => await http.GetAsync("https://upstream.invalid/");
        var error = await fetch.Should().ThrowAsync<ApplicationFailureException>();
        error.Which.NextRetryDelay.Should().Be(TimeSpan.FromSeconds(30));
        server.Requests.Should().Be(1);
    }

    [Test]
    public async Task PhishNetReadsReviewsFromTheNormalObjectEnvelope()
    {
        using var server = new FixtureHttpServer
        {
            Respond = _ => """{"error":false,"data":[{"reviewid":42,"review_text":"A review"}]}""",
        };
        using var http = new HttpClient(
            new IngestionHttpHandler(CancellationToken.None, server.Address)
        );
        var reviews = await new PhishNetApiClient(http).Reviews("2000-01-01", null);
        reviews.Should().ContainSingle().Which.reviewid.Should().Be(42);
    }

    [TestCase("{\"error\":true,\"data\":[]}")]
    [TestCase("{}")]
    public async Task PhishNetErrorsCannotBeMistakenForAnEmptyReviewList(string body)
    {
        using var server = new FixtureHttpServer { Respond = _ => body };
        using var http = new HttpClient(
            new IngestionHttpHandler(CancellationToken.None, server.Address)
        );
        Func<Task> fetch = async () =>
            await new PhishNetApiClient(http).Reviews("2000-01-01", null);
        await fetch.Should().ThrowAsync<InvalidDataException>();
    }
}
