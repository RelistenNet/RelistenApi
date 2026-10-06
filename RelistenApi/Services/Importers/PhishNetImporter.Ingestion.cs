using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;

namespace Relisten.Import;

public partial class PhishNetImporter : IProviderIngestion
{
    private record SourceInput(Source Source, PhishNetApiShow? Show);

    private record FetchedInput(
        PhishNetScrapeResults? Ratings = null,
        string? RedirectUrl = null,
        PhishNetApiReview[]? Reviews = null
    );

    public override void UseIngestionHttpClient(HttpClient client)
    {
        base.UseIngestionHttpClient(client);
        phishNetApiClient = new PhishNetApiClient(client);
    }

    /// <inheritdoc />
    public async Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor)
    {
        if (context.Request.Item != null)
            return new ProviderPage(Array.Empty<ProviderItem>());
        var shows = await phishNetApiClient.Shows(null);
        var sources = await _sourceService.AllForArtistFromPrimary(context.Artist);
        return new ProviderPage(
            sources
                .Where(s =>
                    context.Request.Year == null
                    || s.display_date.StartsWith(context.Request.Year.ToString()!)
                )
                .OrderBy(s => s.display_date)
                .Select(s => new ProviderItem(
                    s.id.ToString(),
                    JsonConvert.SerializeObject(
                        new SourceInput(
                            s,
                            shows.FirstOrDefault(show => show.showdate == s.display_date)
                        )
                    )
                ))
                .ToArray()
        );
    }

    /// <inheritdoc />
    public async Task<ProviderFetch> FetchAsync(
        ProviderContext context,
        ProviderItem item,
        int part
    )
    {
        var source = JsonConvert.DeserializeObject<SourceInput>(item.Body)!.Source;
        var previous =
            context.PreviousFetch == null
                ? null
                : JsonConvert.DeserializeObject<FetchedInput>(context.PreviousFetch.Body);
        if (part == 0 || previous?.RedirectUrl != null)
        {
            if (part >= 50)
                throw new System.IO.InvalidDataException("Phish.net ratings redirect loop.");
            var url =
                previous?.RedirectUrl
                ?? PhishNetRatingsScraper.PhishNetUrlForSource(source.display_date);
            using var response = await http.GetAsync(url, context.CancellationToken);
            // The date lookup redirects to the canonical show page. Each hop is
            // staged so the next request uses another activity/queue slot.
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            {
                var destination = new Uri(
                    response.RequestMessage!.RequestUri!,
                    response.Headers.Location
                );
                return new ProviderFetch(
                    JsonConvert.SerializeObject(
                        new FetchedInput(RedirectUrl: destination.ToString())
                    ),
                    false
                );
            }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
            var ratings = new PhishNetRatingsExtractor(body).ExtractRatings();
            return new ProviderFetch(
                JsonConvert.SerializeObject(new FetchedInput(Ratings: ratings)),
                !ratings.RatingMatched || source.num_reviews == ratings.NumberOfReviewsWritten
            );
        }
        var reviews = (await phishNetApiClient.Reviews(source.display_date, null)).ToArray();
        return new ProviderFetch(JsonConvert.SerializeObject(new FetchedInput(Reviews: reviews)));
    }

    /// <inheritdoc />
    public async Task<ImportStats> ApplyAsync(
        ProviderContext context,
        ProviderItem item,
        ProviderFetch[] fetched
    )
    {
        var source = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<Source>(
                "SELECT * FROM sources WHERE artist_id=@artistId AND id=@id",
                new { artistId = context.Artist.id, id = long.Parse(item.Key) }
            )
        );
        var input = JsonConvert.DeserializeObject<SourceInput>(item.Body)!;
        var shows =
            input.Show == null
                ? new List<PhishNetApiShow>()
                : new List<PhishNetApiShow> { input.Show };
        var inputs = fetched
            .Select(f => JsonConvert.DeserializeObject<FetchedInput>(f.Body)!)
            .ToArray();
        var ratings = inputs.Single(f => f.Ratings != null).Ratings!;
        var reviews =
            inputs.SingleOrDefault(f => f.Reviews != null)?.Reviews
            ?? Array.Empty<PhishNetApiReview>();
        return await ProcessSource(
            context.Artist,
            context.Source,
            source,
            shows,
            null,
            ratings,
            reviews
        );
    }
}
