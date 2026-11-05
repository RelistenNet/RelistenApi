using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Relisten.Api.Models;

namespace Relisten.Import.PhishNet;

/// <summary>
/// Calls Phish.net API v5 following its Security Requirements Addendum (effective 2026-11-01,
/// required by 2027-05-01): the key travels in the X-API-KEY header rather than the URL, and every
/// request identifies Relisten in its User-Agent. The headers are set per request so the HTML
/// ratings scraper, which shares this HttpClient, never sends the key to phish.net pages.
/// </summary>
public class PhishNetApiClient(HttpClient httpClient, string? apiKey)
{
    const string ApiBaseUrl = "https://api.phish.net/v5";
    const string ApiUserAgent =
        "Relisten/1.0 (+https://relisten.net; https://github.com/RelistenNet/RelistenApi)";

    /// <summary>Reads PHISHNET_API_KEY. A missing key fails the first API request, not construction.</summary>
    public static string? ApiKeyFrom(IConfiguration configuration)
    {
        var configured = configuration["PHISHNET_API_KEY"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    static string CleanPhishNetString(string str)
    {
        // I don't know what the heck this garbage is, but we don't want it.
        return str.Replace("â\u0080\u009dÂ", string.Empty)
            .Replace("Â\u009d", string.Empty)
            .Replace("â\u0080\u009c", string.Empty);
    }

    async Task<string> GetApiResponseBody(string path, PerformContext? ctx)
    {
        // The importer is constructed for every scope in the API and worker, so a missing key is
        // reported only when a Phish.net import actually runs.
        if (apiKey == null)
        {
            throw new InvalidOperationException(
                "PHISHNET_API_KEY is not configured; Phish.net API requests require it."
            );
        }

        var url = ApiBaseUrl + path;
        ctx?.WriteLine($"Requesting {url}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-API-KEY", apiKey);
        // A request-level User-Agent replaces the client's default rather than appending to it.
        request.Headers.TryAddWithoutValidation("User-Agent", ApiUserAgent);

        using var resp = await httpClient.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        return CleanPhishNetString(await resp.Content.ReadAsStringAsync());
    }

    public async Task<IList<PhishNetApiShow>> Shows(PerformContext? ctx)
    {
        var page = await GetApiResponseBody("/shows", ctx);
        var shows = JsonConvert.DeserializeObject<PhishNetApiResponse<PhishNetApiShow>>(page);

        if (shows == null || shows.error || shows.data == null)
        {
            throw new System.IO.InvalidDataException(
                "Phish.net returned an invalid show response."
            );
        }

        return shows.data.Where(show => show.artist_name == "Phish").ToList();
    }

    public async Task<IEnumerable<PhishNetApiReview>> Reviews(
        string displayDate,
        PerformContext? ctx
    )
    {
        var page = await GetApiResponseBody($"/reviews/showdate/{displayDate}.json", ctx);
        var reviews = JsonConvert.DeserializeObject<PhishNetApiResponse<PhishNetApiReview>>(page);

        if (reviews == null || reviews.error || reviews.data == null)
        {
            throw new System.IO.InvalidDataException(
                "Phish.net returned an invalid review response."
            );
        }

        return reviews.data;
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
