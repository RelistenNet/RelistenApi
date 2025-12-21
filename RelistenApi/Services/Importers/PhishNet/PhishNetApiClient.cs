using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Relisten.Api.Models;

namespace Relisten.Import.PhishNet;

public class PhishNetApiClient(HttpClient httpClient)
{
    static string CleanPhishNetString(string str)
    {
        // I don't know what the heck this garbage is, but we don't want it.
        return str
            .Replace("â\u0080\u009dÂ", string.Empty)
            .Replace("Â\u009d", string.Empty)
            .Replace("â\u0080\u009c", string.Empty);
    }

    public async Task<IList<PhishNetApiShow>> Shows(PerformContext? ctx)
    {
        const string url = "https://api.phish.net/v5/shows?apikey=C60F490D1358FBBE31DA";
        ctx?.WriteLine($"Requesting {url}");

        var resp = await httpClient.GetAsync(url);
        var page = await resp.Content.ReadAsStringAsync();

        var shows = JsonConvert.DeserializeObject<PhishNetApiResponse<PhishNetApiShow>>(CleanPhishNetString(page));

        return shows?.data?.Where(show => show.artist_name == "Phish").ToList()
               ?? new List<PhishNetApiShow>();
    }

    string PhishNetApiReviewsUrlForDate(string displayDate)
    {
        return
            $"https://api.phish.net/v5/reviews/showdate/{displayDate}.json?apikey=B6570BEDA805B616AB6C";
    }

    public async Task<IEnumerable<PhishNetApiReview>> Reviews(string displayDate, PerformContext? ctx)
    {
        var url = PhishNetApiReviewsUrlForDate(displayDate);
        ctx?.WriteLine($"Requesting {url}");
        var resp = await httpClient.GetAsync(url);
        var page = await resp.Content.ReadAsStringAsync();

        // some shows have no reviews
        if (page.Length == 0 || page[0] == '{')
        {
            return new List<PhishNetApiReview>();
        }

        var reviews =
            JsonConvert.DeserializeObject<PhishNetApiResponse<PhishNetApiReview>>(CleanPhishNetString(page));

        return reviews?.data ?? new List<PhishNetApiReview>();
    }
}


public class PhishNetApiReview
{
    public int reviewid { get; set; }
    public int uid { get; set; }
    public string username { get; set; } = null!;
    public string review_text { get; set; } = null!;
    [JsonConverter(typeof(DateFormatConverter), "yyyy-MM-dd HH:mm:ss")]
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

public class PhishNetApiResponse<T> where T : class
{
    public bool error { get; set; }
    public string error_message { get; set; } = null!;
    public List<T> data { get; set; } = null!;
}
