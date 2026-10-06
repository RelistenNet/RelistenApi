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
using Relisten.Vendor;

namespace Relisten.Import;

public partial class PhishNetImporter : IProviderIngestion
{
    private record SourceInput(Source Source, PhishNetApiShow? Show);

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
        if (part == 0)
        {
            var ratings = await ScrapePhishNetForSource(source, null);
            return new ProviderFetch(
                JsonConvert.SerializeObject(ratings),
                !ratings.RatingMatched || source.num_reviews == ratings.NumberOfReviewsWritten
            );
        }
        var reviews = await phishNetApiClient.Reviews(source.display_date, null);
        return new ProviderFetch(JsonConvert.SerializeObject(reviews));
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
        var ratings = JsonConvert.DeserializeObject<PhishNetScrapeResults>(fetched[0].Body)!;
        var reviews =
            fetched.Length > 1
                ? JsonConvert.DeserializeObject<PhishNetApiReview[]>(fetched[1].Body)!
                : Array.Empty<PhishNetApiReview>();
        return await ProcessSource(
            context.Artist,
            context.Source,
            source,
            shows!,
            null,
            ratings,
            reviews
        );
    }
}
