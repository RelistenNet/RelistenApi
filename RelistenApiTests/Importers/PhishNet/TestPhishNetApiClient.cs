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

            var reviews = (await new PhishNetApiClient(http).Reviews("1983-12-02", null)).ToArray();

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

    private sealed class ReviewResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
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
