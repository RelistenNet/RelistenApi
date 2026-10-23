using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Venues;

/// Where a venue is: a city with its region (a US state or Canadian province, as its postal
/// code) or its country. Key is what blocking compares: "new york|ny|US".
public sealed record Locality(string City, string? Region, string? Country)
{
    public string Key =>
        $"{TitleNormalizer.Normalize(City)}|{Region?.ToLowerInvariant()}|{Country}";
}

/// Normalises venue names and locations the same way for every artist (string-theory
/// docs/matching.md T3): a name key for lookups, and a locality for blocking. Placeholders
/// ("Unknown Location", "TBA") give nothing.
public static partial class VenueNormalizer
{
    public const string Version = "venue-normalize/1";

    private static readonly HashSet<string> Placeholders =
    [
        "unknown",
        "unknown location",
        "unknown venue",
        "tba",
        "tbd",
        "tba tba",
        "n a",
        "na",
        "none",
        "various",
        "various venues",
        "venue",
        "unknown unknown",
    ];

    /// The name key: TitleNormalizer's folding, then a leading "the" dropped and spelling
    /// variants folded (theatre/theater, amphitheatre/amphitheater, centre/center).
    public static string? Key(string? name)
    {
        var key = TitleNormalizer.Normalize(name ?? "");
        if (key.Length == 0 || Placeholders.Contains(key))
        {
            return null;
        }

        var words = key.Split(' ').Select(w => Fold.GetValueOrDefault(w, w)).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    private static readonly Dictionary<string, string> Fold = new()
    {
        ["theatre"] = "theater",
        ["amphitheatre"] = "amphitheater",
        ["centre"] = "center",
        ["ctr"] = "center",
        ["auditorium"] = "auditorium",
        ["aud"] = "auditorium",
        ["univ"] = "university",
        ["u"] = "university",
        ["coliseum"] = "coliseum",
        ["colosseum"] = "coliseum",
    };

    /// A short name made of a longer name's initials: "MSG" for "Madison Square Garden".
    public static bool IsInitialsOf(string shortKey, string fullKey)
    {
        if (shortKey.Contains(' ') || shortKey.Length < 2 || shortKey.Length > 6)
        {
            return false;
        }

        var initials = string.Concat(
            fullKey.Split(' ').Where(w => w is not ("of" or "and" or "the")).Select(w => w[0])
        );
        return initials == shortKey;
    }

    /// Parses "City, ST", "City, State", "City, Province" and "City, Country". Null for a
    /// placeholder or a string with no city.
    public static Locality? ParseLocation(string? location)
    {
        if (
            string.IsNullOrWhiteSpace(location)
            || Placeholders.Contains(TitleNormalizer.Normalize(location))
        )
        {
            return null;
        }

        var fields = location.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        var city = CityAliases.GetValueOrDefault(TitleNormalizer.Normalize(fields[0]), fields[0]);
        if (fields.Count == 1)
        {
            return Placeholders.Contains(TitleNormalizer.Normalize(city))
                ? null
                : new Locality(city, null, null);
        }

        // The last field is the region or country; a middle field ("Cambridge, MA, USA") is the
        // region when the last is a country.
        var last = TitleNormalizer.Normalize(fields[^1]);
        var region = Region(last);
        string? country = region?.Country;
        if (region == null && Countries.TryGetValue(last, out var c))
        {
            country = c;
            region = fields.Count > 2 ? Region(TitleNormalizer.Normalize(fields[^2])) : null;
        }

        // "NY, NY" and "New York, New York": the city is the region's namesake.
        if (
            region != null
            && TitleNormalizer.Normalize(city) is var cityKey
            && (cityKey == region.Code.ToLowerInvariant() || cityKey == region.Name)
        )
        {
            city = region.Code switch
            {
                "NY" => "New York",
                "DC" => "Washington",
                _ => city,
            };
        }

        if (Placeholders.Contains(TitleNormalizer.Normalize(city)))
        {
            return null;
        }

        // A region this table doesn't know stays as written, so two towns of one name are never
        // folded together on a guess: the last field when it isn't a country ("Tokyo, Kanto"),
        // the middle one when it is ("Richmond, NSW, Australia").
        var written =
            country == null ? fields[^1]
            : fields.Count > 2 ? fields[^2]
            : null;
        return new Locality(city, region?.Code ?? written, country);
    }

    private sealed record RegionInfo(string Code, string Name, string Country);

    private static RegionInfo? Region(string key) =>
        RegionsByKey.TryGetValue(key, out var region) ? region : null;

    private static readonly Dictionary<string, string> CityAliases = new()
    {
        ["nyc"] = "New York",
        ["new york city"] = "New York",
        ["ny"] = "New York",
        ["sf"] = "San Francisco",
        ["la"] = "Los Angeles",
        ["philly"] = "Philadelphia",
        ["saint louis"] = "St. Louis",
        ["saint paul"] = "St. Paul",
        ["washington dc"] = "Washington",
        ["washington d c"] = "Washington",
    };

    private static readonly (string Code, string Name, string Country)[] RegionTable =
    [
        ("AL", "alabama", "US"),
        ("AK", "alaska", "US"),
        ("AZ", "arizona", "US"),
        ("AR", "arkansas", "US"),
        ("CA", "california", "US"),
        ("CO", "colorado", "US"),
        ("CT", "connecticut", "US"),
        ("DE", "delaware", "US"),
        ("DC", "district of columbia", "US"),
        ("FL", "florida", "US"),
        ("GA", "georgia", "US"),
        ("HI", "hawaii", "US"),
        ("ID", "idaho", "US"),
        ("IL", "illinois", "US"),
        ("IN", "indiana", "US"),
        ("IA", "iowa", "US"),
        ("KS", "kansas", "US"),
        ("KY", "kentucky", "US"),
        ("LA", "louisiana", "US"),
        ("ME", "maine", "US"),
        ("MD", "maryland", "US"),
        ("MA", "massachusetts", "US"),
        ("MI", "michigan", "US"),
        ("MN", "minnesota", "US"),
        ("MS", "mississippi", "US"),
        ("MO", "missouri", "US"),
        ("MT", "montana", "US"),
        ("NE", "nebraska", "US"),
        ("NV", "nevada", "US"),
        ("NH", "new hampshire", "US"),
        ("NJ", "new jersey", "US"),
        ("NM", "new mexico", "US"),
        ("NY", "new york", "US"),
        ("NC", "north carolina", "US"),
        ("ND", "north dakota", "US"),
        ("OH", "ohio", "US"),
        ("OK", "oklahoma", "US"),
        ("OR", "oregon", "US"),
        ("PA", "pennsylvania", "US"),
        ("RI", "rhode island", "US"),
        ("SC", "south carolina", "US"),
        ("SD", "south dakota", "US"),
        ("TN", "tennessee", "US"),
        ("TX", "texas", "US"),
        ("UT", "utah", "US"),
        ("VT", "vermont", "US"),
        ("VA", "virginia", "US"),
        ("WA", "washington", "US"),
        ("WV", "west virginia", "US"),
        ("WI", "wisconsin", "US"),
        ("WY", "wyoming", "US"),
        ("PR", "puerto rico", "US"),
        ("AB", "alberta", "CA"),
        ("BC", "british columbia", "CA"),
        ("MB", "manitoba", "CA"),
        ("NB", "new brunswick", "CA"),
        ("NL", "newfoundland and labrador", "CA"),
        ("NS", "nova scotia", "CA"),
        ("ON", "ontario", "CA"),
        ("PE", "prince edward island", "CA"),
        ("QC", "quebec", "CA"),
        ("SK", "saskatchewan", "CA"),
        ("YT", "yukon", "CA"),
        ("NT", "northwest territories", "CA"),
        ("NU", "nunavut", "CA"),
    ];

    private static readonly Dictionary<string, RegionInfo> RegionsByKey = RegionTable
        .SelectMany(r =>
            new[]
            {
                (Key: r.Code.ToLowerInvariant(), Info: new RegionInfo(r.Code, r.Name, r.Country)),
                (Key: r.Name, Info: new RegionInfo(r.Code, r.Name, r.Country)),
            }
        )
        .Concat([
            ("d c", new RegionInfo("DC", "district of columbia", "US")),
            ("pq", new RegionInfo("QC", "quebec", "CA")),
            ("calif", new RegionInfo("CA", "california", "US")),
            ("mass", new RegionInfo("MA", "massachusetts", "US")),
            ("penn", new RegionInfo("PA", "pennsylvania", "US")),
        ])
        .GroupBy(r => r.Item1)
        .ToDictionary(g => g.Key, g => g.First().Item2);

    private static readonly Dictionary<string, string> Countries = new()
    {
        ["usa"] = "US",
        ["us"] = "US",
        ["united states"] = "US",
        ["united states of america"] = "US",
        ["canada"] = "CA",
        ["uk"] = "GB",
        ["united kingdom"] = "GB",
        ["england"] = "GB",
        ["scotland"] = "GB",
        ["wales"] = "GB",
        ["northern ireland"] = "GB",
        ["ireland"] = "IE",
        ["germany"] = "DE",
        ["west germany"] = "DE",
        ["france"] = "FR",
        ["netherlands"] = "NL",
        ["the netherlands"] = "NL",
        ["holland"] = "NL",
        ["belgium"] = "BE",
        ["denmark"] = "DK",
        ["sweden"] = "SE",
        ["norway"] = "NO",
        ["finland"] = "FI",
        ["spain"] = "ES",
        ["portugal"] = "PT",
        ["italy"] = "IT",
        ["switzerland"] = "CH",
        ["austria"] = "AT",
        ["czech republic"] = "CZ",
        ["poland"] = "PL",
        ["japan"] = "JP",
        ["australia"] = "AU",
        ["new zealand"] = "NZ",
        ["mexico"] = "MX",
        ["brazil"] = "BR",
        ["argentina"] = "AR",
        ["jamaica"] = "JM",
        ["egypt"] = "EG",
        ["israel"] = "IL",
        ["iceland"] = "IS",
    };
}
