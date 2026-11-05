using System.Globalization;
using System.Net;
using FluentAssertions;
using Relisten.Import.PhishNet;

namespace RelistenApiTests.Importers.PhishNet;

[TestFixture]
public class TestPhishNetApiClient
{
    [Test]
    public async Task ReviewsAcceptUpstreamOffsetsAndLegacyDatesRegardlessOfCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            using var handler = new ReviewResponseHandler();
            using var http = new HttpClient(handler);

            var reviews = (
                await new PhishNetApiClient(http, "test-key").Reviews("1983-12-02", null)
            ).ToArray();

            reviews
                .Select(review => review.posted_at)
                .Should()
                .Equal(
                    new DateTime(2010, 9, 27, 21, 30, 43, DateTimeKind.Utc),
                    new DateTime(2010, 9, 27, 21, 30, 43, DateTimeKind.Utc),
                    new DateTime(2010, 9, 27, 17, 30, 43, DateTimeKind.Unspecified)
                );
            reviews
                .Select(review => review.posted_at.Kind)
                .Should()
                .Equal(DateTimeKind.Utc, DateTimeKind.Utc, DateTimeKind.Unspecified);
            reviews[0].review_text.Should().Be("A review with the upstream hour-only offset.");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public async Task SendsTheApiKeyInAHeaderAndIdentifiesRelisten()
    {
        using var handler = new ReviewResponseHandler();
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (scraper default)");

        await new PhishNetApiClient(http, "test-key").Reviews("1997-11-22", null);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request
            .RequestUri!.ToString()
            .Should()
            .Be("https://api.phish.net/v5/reviews/showdate/1997-11-22.json");
        request.Headers.GetValues("X-API-KEY").Should().Equal("test-key");
        request.Headers.UserAgent.ToString().Should().StartWith("Relisten/");
    }

    [Test]
    public async Task AMissingApiKeyFailsBeforeAnyRequest()
    {
        using var handler = new ReviewResponseHandler();
        using var http = new HttpClient(handler);

        Func<Task> fetch = async () =>
            await new PhishNetApiClient(http, null).Reviews("1997-11-22", null);

        await fetch
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*PHISHNET_API_KEY*");
        handler.Requests.Should().BeEmpty();
    }

    private sealed class ReviewResponseHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);

            var fixture = Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                "reviews-dates.json"
            );

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    await File.ReadAllTextAsync(fixture, cancellationToken)
                ),
            };
        }
    }
}
