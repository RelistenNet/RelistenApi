using FluentAssertions;
using Relisten.Import.PhishNet;

namespace RelistenApiTests.Importers.PhishNet;

[TestFixture]
public class TestPhishNetRatingsExtractor
{
    string over500RatingsSetlistHtml;
    string under50RatingsSetlistHtml;

    [SetUp]
    public void SetUp()
    {
        over500RatingsSetlistHtml = TestUtils.ReadFixture(@"PhishNet/setlist-1997-11-22.html");
        under50RatingsSetlistHtml = TestUtils.ReadFixture(@"PhishNet/setlist-1992-11-21.html");
    }

    [Test]
    public void CanExtractContentsOver500()
    {
        var scraper = new PhishNetRatingsExtractor(over500RatingsSetlistHtml);
        var results = scraper.ExtractRatings();

        results.Should().BeEquivalentTo(new PhishNetScrapeResults
        {
            RatingAverage = 4.636m, RatingVotesCast = 500, NumberOfReviewsWritten = 18
        });
    }

    [Test]
    public void CanExtractContentsUnder50()
    {
        var scraper = new PhishNetRatingsExtractor(under50RatingsSetlistHtml);
        var results = scraper.ExtractRatings();

        results.Should().BeEquivalentTo(new PhishNetScrapeResults
        {
            RatingAverage = 4.268m, RatingVotesCast = 50, NumberOfReviewsWritten = 4
        });
    }
}
