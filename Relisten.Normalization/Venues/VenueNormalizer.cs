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

    /// The state or country part: two towns in different regions are different places.
    public string? RegionKey =>
        Region != null && Country != null ? $"{Region.ToLowerInvariant()}|{Country}" : Country;

    /// A recognised state or country: the town is a town, not a seat or a typed note.
    public bool Trusted => Country != null;
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
        // A bracketed note is about the recording, not the place ("[24bit]", "[SBD]").
        var key = TitleNormalizer.Normalize(Bracketed().Replace(name ?? "", " "));
        if (key.Length == 0 || Placeholders.Contains(key))
        {
            return null;
        }

        var words = JoinInitials(key.Split(' ')).Select(w => Fold.GetValueOrDefault(w, w)).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[[^\]]*\]")]
    private static partial System.Text.RegularExpressions.Regex Bracketed();

    /// Letters written with periods are one word: "U.S." → "us", "S.M.U." → "smu" (and never
    /// "university s").
    private static IEnumerable<string> JoinInitials(string[] words)
    {
        var run = "";
        foreach (var word in words)
        {
            if (word.Length == 1 && char.IsLetter(word[0]))
            {
                run += word;
                continue;
            }

            if (run.Length > 0)
            {
                yield return run;
                run = "";
            }

            yield return word;
        }

        if (run.Length > 0)
        {
            yield return run;
        }
    }

    /// A two-letter code that names a country as well as a state or province: "DE" (Delaware,
    /// Germany), "CA" (California, Canada), "NL" (Newfoundland, the Netherlands).
    public static bool IsCountryCode(string code) =>
        Countries.ContainsValue(code.ToUpperInvariant());

    /// The words that name the place: spellings and abbreviations folded ("St." and "Street",
    /// "Amph." and "Ampitheatre"), possessives and plurals dropped ("Smith's", "Red Rocks"),
    /// and the small words gone ("the", "at", "of").
    public static IReadOnlyList<string> Words(string key)
    {
        var words = key.Split(' ')
            .Select(w => Canon.GetValueOrDefault(w, w))
            .Select(w => w.Length > 3 && w[^1] == 's' && w[^2] != 's' ? w[..^1] : w)
            .Where(w => !SmallWords.Contains(w))
            .ToList();
        return words.Count > 0 ? words : key.Split(' ');
    }

    /// What identity compares: two keys share an identity when they are one name. The words
    /// without spaces ("1stBank Center", "Barrel House" and "9:30 Club" are "1st Bank Center",
    /// "Barrelhouse" and "930 Club"), and the words in any order ("Wakarusa - Main Stage" is
    /// "Main Stage - Wakarusa", "Notre Dame University" is "University of Notre Dame").
    public static IReadOnlyList<string> Identities(string key)
    {
        var words = Words(key);
        return
        [
            "c:" + string.Concat(words),
            "s:" + string.Join(' ', words.Order(StringComparer.Ordinal)),
        ];
    }

    public static bool SameName(string a, string b) => Identities(a).Intersect(Identities(b)).Any();

    private static readonly HashSet<string> SmallWords = ["the", "at", "of", "and", "in", "on"];

    private static readonly Dictionary<string, string> Canon = new()
    {
        ["amphitheater"] = "amphitheater",
        ["ampitheater"] = "amphitheater",
        ["ampitheatre"] = "amphitheater",
        ["amphitheatre"] = "amphitheater",
        ["amphitheter"] = "amphitheater",
        ["amph"] = "amphitheater",
        ["amp"] = "amphitheater",
        ["street"] = "st",
        ["saint"] = "st",
        ["avenue"] = "ave",
        ["co"] = "company",
        ["bros"] = "brothers",
        ["col"] = "college",
        ["coll"] = "college",
        ["mount"] = "mt",
        ["fort"] = "ft",
        ["nite"] = "night",
        ["dorm"] = "dormitory",
        ["pavillion"] = "pavilion",
        ["brewing"] = "brewery",
        ["olde"] = "old",
        ["intl"] = "international",
        ["natl"] = "national",
        ["mem"] = "memorial",
        ["hs"] = "high school",
    };

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

        // "Dayton Ohio/Huber Heights Ohio": the first place named.
        location = location.Split('/')[0];
        var fields = location.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        // No comma before the state: "Phoenix AZ", "Huber Heights Ohio", "Broomfield. CO".
        if (fields.Count == 1 && TrailingRegion(fields[0]) is { } split)
        {
            fields = [split.City, split.Region];
        }

        // A seat, a date or a typed note is not a town ("Stand, Seat 2", "Row R, On Aisle)"),
        // and neither is a state alone ("CO").
        if (
            NotATown(fields[0])
            || (fields.Count == 1 && Region(TitleNormalizer.Normalize(fields[0])) != null)
        )
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

        // A misspelled state ("Morrison, Colorade, USA"): the one state name a typo away.
        var regionField =
            country != null && region == null && fields.Count > 2 ? fields[^2]
            : country == null && region == null ? fields[^1]
            : null;
        if (regionField != null && NearRegion(TitleNormalizer.Normalize(regionField)) is { } near)
        {
            region = near;
            country = near.Country;
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

    private static readonly HashSet<string> SeatWords =
    [
        "row",
        "seat",
        "seats",
        "section",
        "sec",
        "aisle",
        "stand",
        "balcony",
        "floor",
        "orchestra",
        "loge",
        "fob",
        "sbd",
        "aud",
        "taper",
        "tapers",
        "venue",
        "show",
        "early",
        "late",
    ];

    private static bool NotATown(string field)
    {
        var key = TitleNormalizer.Normalize(field);
        return key.Length == 0
            || key.Any(char.IsDigit)
            || key.Split(' ').Any(SeatWords.Contains)
            || Placeholders.Contains(key);
    }

    /// "Phoenix AZ" → (Phoenix, AZ); "Huber Heights Ohio" → (Huber Heights, Ohio); "Broomfield.
    /// CO" → (Broomfield, CO). Null when the last words name no known state or province.
    private static (string City, string Region)? TrailingRegion(string field)
    {
        var words = field.Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(3, words.Length - 1); take >= 1; take--)
        {
            var region = string.Join(' ', words[^take..]);
            if (Region(TitleNormalizer.Normalize(region)) != null)
            {
                return (string.Join(' ', words[..^take]), region);
            }
        }

        return null;
    }

    private sealed record RegionInfo(string Code, string Name, string Country);

    private static RegionInfo? NearRegion(string key)
    {
        if (key.Length < 5)
        {
            return null;
        }

        var limit = key.Length >= 8 ? 2 : 1;
        var near = RegionTable
            .Where(r => Titles.TitleGrouper.BoundedLevenshtein(r.Name, key, limit) <= limit)
            .Select(r => new RegionInfo(r.Code, r.Name, r.Country))
            .Take(2)
            .ToList();
        return near.Count == 1 ? near[0] : null;
    }

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
        ["neth"] = "NL",
        ["nl"] = "NL",
        ["deutschland"] = "DE",
        ["gb"] = "GB",
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
