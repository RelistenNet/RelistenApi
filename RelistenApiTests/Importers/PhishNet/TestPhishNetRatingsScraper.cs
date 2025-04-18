using FluentAssertions;
using Relisten.Import.PhishNet;

namespace RelistenApiTests.Importers.PhishNet;

[TestFixture]
public class TestPhishNetRatingsScraper
{
    HttpClient http;

    [SetUp]
    public void SetUp()
    {
        var factory = new PhishNetHttpClientFactory();
        http = factory.HttpClient;
    }

    [TearDown]
    public void TearDown()
    {
        http.Dispose();
    }

    [Test]
    public async Task CanScrapeOver500()
    {
        var scraper = new PhishNetRatingsScraper(http, "1997-11-22");
        var results = await scraper.ScrapeRatings();

        results.RatingAverage.Should().BeGreaterThan(0);
        results.RatingVotesCast.Should().BeGreaterThan(0);
        results.NumberOfReviewsWritten.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task CanScrapeUnder50()
    {
        var scraper = new PhishNetRatingsScraper(http, "1992-11-23");
        var results = await scraper.ScrapeRatings();

        results.RatingAverage.Should().BeGreaterThan(0);
        results.RatingVotesCast.Should().BeGreaterThan(0);
        results.NumberOfReviewsWritten.Should().BeGreaterThan(0);
    }
}
