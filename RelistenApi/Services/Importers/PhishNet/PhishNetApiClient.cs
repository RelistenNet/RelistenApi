using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Import.PhishNet;

public class PhishNetApiClient(
    HttpClient httpClient,
    NormalizationProviderInputService? normalizationInputs = null
)
{
    static string CleanPhishNetString(string str)
    {
        // I don't know what the heck this garbage is, but we don't want it.
        return str.Replace("â\u0080\u009dÂ", string.Empty)
            .Replace("Â\u009d", string.Empty)
            .Replace("â\u0080\u009c", string.Empty);
    }

    public async Task<IList<PhishNetApiShow>> Shows(
        PerformContext? ctx,
        CancellationToken cancellationToken = default
    )
    {
        const string url = "https://api.phish.net/v5/shows?apikey=C60F490D1358FBBE31DA";
        // Stable evidence keys and logs identify the route, never its authentication query.
        ctx?.WriteLine("Requesting Phish.net shows");
        var shows = await FetchAsync<PhishNetApiShow>(
            url,
            new("phish.net", "shows", "v5"),
            cancellationToken
        );
        return shows.Where(show => show.artist_name == "Phish").ToList();
    }

    string PhishNetApiReviewsUrlForDate(string displayDate)
    {
        return $"https://api.phish.net/v5/reviews/showdate/{displayDate}.json?apikey=B6570BEDA805B616AB6C";
    }

    public async Task<IEnumerable<PhishNetApiReview>> Reviews(
        string displayDate,
        PerformContext? ctx,
        CancellationToken cancellationToken = default
    )
    {
        ctx?.WriteLine($"Requesting Phish.net reviews for {displayDate}");
        return await FetchAsync<PhishNetApiReview>(
            PhishNetApiReviewsUrlForDate(displayDate),
            new("phish.net", "reviews", displayDate),
            cancellationToken
        );
    }

    private async Task<IList<T>> FetchAsync<T>(
        string url,
        NormalizationResourceKey resource,
        CancellationToken cancellationToken
    )
        where T : class
    {
        async Task<NormalizationProviderResponse<IList<T>>> Fetch(CancellationToken ct)
        {
            using var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsByteArrayAsync(ct);
            var envelope = JsonConvert.DeserializeObject<PhishNetApiResponse<T>>(
                CleanPhishNetString(await response.Content.ReadAsStringAsync(ct))
            );
            if (envelope == null || envelope.error || envelope.data == null)
            {
                throw new System.IO.InvalidDataException("Phish.net returned an invalid response.");
            }

            return new(
                envelope.data,
                payload,
                response.Content.Headers.ContentType?.ToString() ?? "application/json",
                response.Headers.ETag?.ToString()
            );
        }

        // Standalone parser callers retain their existing constructor. Importers
        // always supply capture, which retains bytes before string repair and filtering.
        return normalizationInputs == null
            ? (await Fetch(cancellationToken)).Value
            : await normalizationInputs.FetchAsync(
                resource,
                "phish-net-http-v1",
                Fetch,
                cancellationToken
            );
    }
}

public class PhishNetApiReview
{
    public int reviewid { get; set; }
    public int uid { get; set; }
    public string username { get; set; } = null!;
    public string review_text { get; set; } = null!;

    [JsonConverter(typeof(PhishNetReviewDateConverter))]
    public DateTime posted_at { get; set; }
    public int score { get; set; }
    public int showid { get; set; }
    public string showdate { get; set; } = null!;
    public string showyear { get; set; } = null!;
    public string permalink { get; set; } = null!;
    public int artistid { get; set; }
    public string artist_name { get; set; } = null!;
    public string venue { get; set; } = null!;
    public string city { get; set; } = null!;
    public string state { get; set; } = null!;
    public string country { get; set; } = null!;
}

/// <summary>Accepts Phish.net review dates with either an hour offset or no offset.</summary>
internal sealed class PhishNetReviewDateConverter : IsoDateTimeConverter
{
    public PhishNetReviewDateConverter()
    {
        Culture = CultureInfo.InvariantCulture;
        // Explicit offsets describe an instant. Normalize them before persistence;
        // older offset-free values retain their existing unspecified-time behavior.
        DateTimeStyles = DateTimeStyles.AdjustToUniversal;
    }
}

public class PhishNetApiShow
{
    public int showid { get; set; }
    public string showyear { get; set; } = null!;
    public int showmonth { get; set; }
    public int showday { get; set; }
    public string showdate { get; set; } = null!;
    public string permalink { get; set; } = null!;
    public int exclude_from_stats { get; set; }
    public int? venueid { get; set; }
    public string setlist_notes { get; set; } = null!;
    public string venue { get; set; } = null!;
    public string city { get; set; } = null!;
    public string state { get; set; } = null!;
    public string country { get; set; } = null!;
    public int artistid { get; set; }
    public string artist_name { get; set; } = null!;
    public int? tourid { get; set; }
    public string tour_name { get; set; } = null!;
    public object? created_at { get; set; }
    public string updated_at { get; set; } = null!;
}

public class PhishNetApiResponse<T>
    where T : class
{
    public bool error { get; set; }
    public string error_message { get; set; } = null!;
    public List<T> data { get; set; } = null!;
}
