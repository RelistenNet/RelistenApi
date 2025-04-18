using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Relisten.Import.PhishNet;

public class PhishNetRatingsScraper(HttpClient httpClient, string displayDate)
{
    public static string PhishNetUrlForSource(string displayDate)
    {
        return "https://phish.net/setlists/?d=" + displayDate;
    }

    public async Task<PhishNetScrapeResults> ScrapeRatings()
    {
        var url = PhishNetUrlForSource(displayDate);
        var resp = await httpClient.GetAsync(url);
        var page = await resp.Content.ReadAsStringAsync();

        var extractor = new PhishNetRatingsExtractor(page);

        return extractor.ExtractRatings();
    }
}

public class PhishNetScrapeResults
{
    public decimal RatingAverage { get; init; }
    public int RatingVotesCast { get; init; }
    public int NumberOfReviewsWritten { get; init; }
}

public partial class PhishNetRatingsExtractor(string pageContents)
{
    string PageContents { get; } = pageContents;

    private static readonly Regex PhishNetRatingScraper =
        PhishNetRatingScraperRegex();
    [GeneratedRegex(@"Overall: (?<AverageRating>[\d.]+)\/5 \(>?<?(?<VotesCast>\d+) ratings\)")]
    private static partial Regex PhishNetRatingScraperRegex();

    private static readonly Regex PhishNetReviewCountScraper = PhishNetReviewCountScraperRegex();
    [GeneratedRegex(@"class='tpc-comment review'")]
    private static partial Regex PhishNetReviewCountScraperRegex();

    public PhishNetScrapeResults ExtractRatings()
    {
        var ratingMatches = PhishNetRatingScraper.Match(PageContents);

        return new PhishNetScrapeResults
        {
            RatingAverage = ImporterUtils.TryParseDecimal(ratingMatches.Groups["AverageRating"].Value),
            RatingVotesCast = ImporterUtils.TryParseInt(ratingMatches.Groups["VotesCast"].Value),
            NumberOfReviewsWritten = PhishNetReviewCountScraper.Matches(PageContents).Count
        };
    }
}
