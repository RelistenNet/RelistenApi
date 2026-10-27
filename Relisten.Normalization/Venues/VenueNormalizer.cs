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
        $"{VenueNormalizer.Normalize(City)}|{Region?.ToLowerInvariant()}|{Country}";

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
    /// Lowercase words without punctuation or accents, as written: venues and places keep
    /// "st", "mt" and "n" (titles' "saint", "mount", "and" would make Montana "mount").
    public static string Normalize(string text) =>
        TitleNormalizer.Normalize(QuoteApostrophe().Replace(text, ""), tokens: false);

    // A double quote typed for an apostrophe inside a word: Slim"s.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\p{L})""(?=s\b)")]
    private static partial System.Text.RegularExpressions.Regex QuoteApostrophe();

    public static string? Key(string? name)
    {
        // A bracketed note is about the recording, not the place ("[24bit]", "[SBD]").
        var key = Normalize(
            LetterAmpersand()
                .Replace(
                    RecordingNotes().Replace(Bracketed().Replace(name ?? "", " "), " "),
                    "$1$2"
                )
        );
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

    [System.Text.RegularExpressions.GeneratedRegex(@"\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex Parenthesised();

    // "AT&T", "R&B": short letter groups joined by "&" are one word, not "at and t".
    [System.Text.RegularExpressions.GeneratedRegex(@"\b([A-Za-z]{1,3})&([A-Za-z]{1,3})\b")]
    private static partial System.Text.RegularExpressions.Regex LetterAmpersand();

    // Notes about the recording or the set written into a venue name: "24 bit source",
    // "FOB ORTF", "SBD", "late show", "1st set".
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(\d+\s*-?\s*bit|sbd|aud|fob|ortf|matrix|flac|mp3|source|(early|late)\s+shows?|(1st|2nd|3rd|first|second|third)\s+set)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )]
    private static partial System.Text.RegularExpressions.Regex RecordingNotes();

    /// The words of a place's town, state and country, which say nothing about which venue
    /// a name is: "chicago", "il", "illinois", "us", "usa".
    public static IReadOnlySet<string> PlaceWords(Locality locality)
    {
        var words = RegionWords(locality).ToHashSet();
        words.UnionWith(Normalize(locality.City).Split(' '));
        words.ExceptWith(["of", "the", "new", "north", "south", "east", "west", "united"]);
        return words;
    }

    /// A name without the place written into it: the town as a phrase ("little rock", never
    /// "rock" alone), the state's and country's words one by one. Null when nothing is left.
    public static string? WithoutPlace(string key, Locality locality)
    {
        var city = Normalize(locality.City);
        var rest = $" {key} ".Replace($" {city} ", " ", StringComparison.Ordinal);
        var region = RegionWords(locality);
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !region.Contains(w))
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : null;
    }

    private static HashSet<string> RegionWords(Locality locality)
    {
        var words = new HashSet<string>();
        if (locality.Region != null)
        {
            words.Add(locality.Region.ToLowerInvariant());
            foreach (
                var r in RegionTable.Where(r =>
                    r.Code == locality.Region && r.Country == locality.Country
                )
            )
            {
                words.UnionWith(r.Name.Split(' '));
            }
        }

        if (locality.Country != null)
        {
            words.Add(locality.Country.ToLowerInvariant());
            words.UnionWith(
                Countries.Where(c => c.Value == locality.Country).SelectMany(c => c.Key.Split(' '))
            );
        }

        words.ExceptWith(["of", "the"]);
        return words;
    }

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
    // Memoised: the matcher compares names pairwise, hundreds of thousands of times.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        IReadOnlyList<string>
    > WordsOf = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        IReadOnlyList<string>
    > IdentitiesOf = new();

    public static IReadOnlyList<string> Words(string key) => WordsOf.GetOrAdd(key, ComputeWords);

    private static IReadOnlyList<string> ComputeWords(string key)
    {
        var words = key.Split(' ')
            .Select(w => Canon.GetValueOrDefault(w, w))
            .Select(w => w.Length > 3 && w[^1] == 's' && w[^2] != 's' ? w[..^1] : w)
            .Select(VenueWord)
            .Where(w => !SmallWords.Contains(w))
            .ToList();
        return words.Count > 0 ? words : key.Split(' ');
    }

    /// A common venue word misspelled is that word ("threatre", "auditorum", "colliseum"):
    /// one letter off. Other words stay as written.
    private static string VenueWord(string w)
    {
        if (w.Length < 6 || VenueWords.ContainsKey(w))
        {
            return VenueWords.GetValueOrDefault(w, w);
        }

        // One letter only: two would make "backroom" a "ballroom".
        const int limit = 1;
        var near = VenueWords
            .Where(v => Math.Abs(v.Key.Length - w.Length) <= limit)
            .Where(v => Titles.TitleGrouper.BoundedLevenshtein(v.Key, w, limit) <= limit)
            .Select(v => v.Value)
            .Distinct()
            .Take(2)
            .ToList();
        return near.Count == 1 ? near[0] : w;
    }

    // Each spelling a typo is measured from → the word it is.
    private static readonly Dictionary<string, string> VenueWords = new()
    {
        ["theater"] = "theater",
        ["theatre"] = "theater",
        ["amphitheater"] = "amphitheater",
        ["amphitheatre"] = "amphitheater",
        ["auditorium"] = "auditorium",
        ["coliseum"] = "coliseum",
        ["pavilion"] = "pavilion",
        ["ballroom"] = "ballroom",
        ["stadium"] = "stadium",
        ["fairground"] = "fairground",
        ["fieldhouse"] = "fieldhouse",
        ["playhouse"] = "playhouse",
        ["center"] = "center",
        ["centre"] = "center",
    };

    /// How a town is shown: the most used spelling written with capitals ("San Francisco",
    /// never a row's "san francisco"), else the name with each word capitalised.
    public static string DisplayCity(IEnumerable<string> spellings)
    {
        var list = spellings.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        var written = list.Where(c => c.Any(char.IsUpper))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
        return written
            ?? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                list.FirstOrDefault() ?? ""
            );
    }

    /// What identity compares: two keys share an identity when they are one name. The words
    /// without spaces ("1stBank Center", "Barrel House" and "9:30 Club" are "1st Bank Center",
    /// "Barrelhouse" and "930 Club"), and the words in any order ("Wakarusa - Main Stage" is
    /// "Main Stage - Wakarusa", "Notre Dame University" is "University of Notre Dame").
    public static IReadOnlyList<string> Identities(string key) =>
        IdentitiesOf.GetOrAdd(key, ComputeIdentities);

    private static IReadOnlyList<string> ComputeIdentities(string key)
    {
        var words = Words(key);
        // Glued words fold too: "GeorgiaTheatre" is "Georgia Theatre".
        var compact = string.Concat(words)
            .Replace("theatre", "theater")
            .Replace("centre", "center");
        return ["c:" + compact, "s:" + string.Join(' ', words.Order(StringComparer.Ordinal))];
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
        if (string.IsNullOrWhiteSpace(location) || Placeholders.Contains(Normalize(location)))
        {
            return null;
        }

        // "Dayton Ohio/Huber Heights Ohio": the first place named; a note in brackets
        // ("Englewood, CO (Denver)") is not part of it.
        location = Parenthesised().Replace(location.Split('/')[0], " ");
        var fields = location.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        // No comma before the state: "Phoenix AZ", "Huber Heights Ohio", "Broomfield. CO".
        if (fields.Count == 1 && TrailingRegion(fields[0]) is { } split)
        {
            fields =
                split.Country == null
                    ? [split.City, split.Region]
                    : [split.City, split.Region, split.Country];
        }

        // A seat, a date or a typed note is not a town ("Stand, Seat 2", "Row R, On Aisle)"),
        // and neither is a state alone ("CO").
        if (fields.Count == 1 && (NotATown(fields[0]) || Region(Normalize(fields[0])) != null))
        {
            return null;
        }

        if (fields.Count == 1)
        {
            var only = CityAliases.GetValueOrDefault(Normalize(fields[0]), fields[0]);
            return Placeholders.Contains(Normalize(only)) ? null : new Locality(only, null, null);
        }

        // "Dallas, TX USA", "Perth, WA Australia": a region and its country in one field,
        // split in two so the country decides what the code means.
        if (
            fields.Count >= 2
            && Normalize(fields[^1]).Split(' ') is { Length: >= 2 } lastWords
            && Countries.TryGetValue(lastWords[^1], out var written2)
            && string.Join(' ', lastWords[..^1]) is var regionWords
            && (RegionIn(regionWords, written2) ?? Region(regionWords)) != null
        )
        {
            fields[^1] = regionWords;
            fields.Add(lastWords[^1]);
        }

        // A note after the region ("Manchester, TN, Bonnaroo 2015") is not a place.
        while (
            fields.Count > 2
            && !Recognised(Normalize(fields[^1]))
            && Recognised(Normalize(fields[^2]))
        )
        {
            fields.RemoveAt(fields.Count - 1);
        }

        // The last field is the region or country; a middle field ("Cambridge, MA, USA") is the
        // region when the last is a country.
        var last = Normalize(fields[^1]);
        var region = Region(last);
        string? country = region?.Country;
        if (region == null && Countries.TryGetValue(last, out var c))
        {
            // The region as that country has it: "WA" is Western Australia beside Australia.
            country = c;
            region = fields.Count > 2 ? RegionIn(Normalize(fields[^2]), c) : null;
        }

        // A misspelled state ("Morrison, Colorade, USA"): the one state name a typo away.
        var regionField =
            country != null && region == null && fields.Count > 2 ? fields[^2]
            : country == null && region == null ? fields[^1]
            : null;
        if (regionField != null && NearRegion(Normalize(regionField)) is { } near)
        {
            region = near;
            country = near.Country;
        }

        // The town is the field just before the region and country ("Montage Mountain,
        // Scranton, PA" is in Scranton); earlier fields name a place or a neighbourhood.
        var trailing = country != null && fields.Count > 2 && Countries.ContainsKey(last) ? 2 : 1;
        var cityField = fields[Math.Max(0, fields.Count - 1 - trailing)];
        if (NotATown(cityField))
        {
            return null;
        }

        var city = CityAliases.GetValueOrDefault(Normalize(cityField), cityField);

        // "NY, NY" and "New York, New York": the city is the region's namesake.
        if (
            region != null
            && Normalize(city) is var cityKey
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

        if (Placeholders.Contains(Normalize(city)))
        {
            return null;
        }

        // A region this table doesn't know stays as written, so two towns of one name are never
        // folded together on a guess: the last field when it isn't a country ("Tokyo, Kanto"),
        // the middle one when it is ("Richmond, NSW, Australia").
        // A middle field that is the country again ("London, England, UK") is no region.
        var written =
            country == null ? fields[^1]
            : fields.Count > 2
            && !(Countries.TryGetValue(Normalize(fields[^2]), out var again) && again == country)
                ? fields[^2]
            : null;
        return new Locality(city, region?.Code ?? written, country);
    }

    /// A row with no usable location often has it in the name ("Pepsi Center - Denver, CO",
    /// "Shorefront Park, Patchogue, NY"): the part after a dash, or the whole name, when it ends
    /// with a known state or country. Null otherwise ("HOB Cleveland" names no state).
    public static Locality? LocationInName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.Contains(','))
        {
            return null;
        }

        var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        var place = (dash >= 0 ? ParseLocation(name[(dash + 3)..]) : null)
            is { Trusted: true } afterDash
            ? afterDash
            : ParseLocation(name);
        return place is { Trusted: true } ? place : null;
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
        var key = Normalize(field);
        return key.Length == 0
            || key.Any(char.IsDigit)
            || key.Split(' ').Any(SeatWords.Contains)
            || Placeholders.Contains(key);
    }

    /// "Phoenix AZ" → (Phoenix, AZ); "Huber Heights Ohio" → (Huber Heights, Ohio); "Broomfield.
    /// CO" → (Broomfield, CO). Null when the last words name no known state or province.
    private static (string City, string Region, string? Country)? TrailingRegion(string field)
    {
        var words = field.Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "Winnipeg Manitoba Canada", "Perth WA Australia": a country after the region is kept
        // apart, and decides what the region means.
        string? country = null;
        if (words.Length > 2 && Countries.ContainsKey(Normalize(words[^1])))
        {
            country = words[^1];
            words = words[..^1];
        }

        for (var take = Math.Min(3, words.Length - 1); take >= 1; take--)
        {
            var region = string.Join(' ', words[^take..]);
            var key = Normalize(region);
            if (
                (country == null ? Region(key) : RegionIn(key, Countries[Normalize(country)]))
                != null
            )
            {
                return (string.Join(' ', words[..^take]), region, country);
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

    /// "N.Y." is "NY".
    /// A region of one country ("wa" in AU is Western Australia, in US Washington).
    private static RegionInfo? RegionIn(string key, string country) =>
        RegionsByKeyAndCountry.TryGetValue((key, country), out var region) ? region
        : key.Length <= 3
        && RegionsByKeyAndCountry.TryGetValue((key.Replace(" ", ""), country), out region)
            ? region
        : null;

    private static RegionInfo? Region(string key) =>
        RegionsByKey.TryGetValue(key, out var region) ? region
        : key.Length <= 3 && RegionsByKey.TryGetValue(key.Replace(" ", ""), out region) ? region
        : null;

    private static bool Recognised(string key) =>
        Region(key) != null || Countries.ContainsKey(key) || NearRegion(key) != null;

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

    private static readonly List<(string Key, RegionInfo Info)> RegionEntries = RegionTable
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
            // Australia's states (WA and NT stay Washington and the Northwest Territories).
            ("nsw", new RegionInfo("NSW", "new south wales", "AU")),
            ("new south wales", new RegionInfo("NSW", "new south wales", "AU")),
            ("vic", new RegionInfo("VIC", "victoria", "AU")),
            ("victoria", new RegionInfo("VIC", "victoria", "AU")),
            ("qld", new RegionInfo("QLD", "queensland", "AU")),
            ("queensland", new RegionInfo("QLD", "queensland", "AU")),
            ("tas", new RegionInfo("TAS", "tasmania", "AU")),
            ("tasmania", new RegionInfo("TAS", "tasmania", "AU")),
            ("act", new RegionInfo("ACT", "australian capital territory", "AU")),
            (
                "australian capital territory",
                new RegionInfo("ACT", "australian capital territory", "AU")
            ),
            ("western australia", new RegionInfo("WA", "western australia", "AU")),
            ("south australia", new RegionInfo("SA", "south australia", "AU")),
            ("northern territory", new RegionInfo("NT", "northern territory", "AU")),
            // Codes another country has first; RegionIn finds them beside their country.
            ("wa", new RegionInfo("WA", "western australia", "AU")),
            ("sa", new RegionInfo("SA", "south australia", "AU")),
            ("nt", new RegionInfo("NT", "northern territory", "AU")),
            ("cal", new RegionInfo("CA", "california", "US")),
            ("tenn", new RegionInfo("TN", "tennessee", "US")),
            ("fla", new RegionInfo("FL", "florida", "US")),
            ("ill", new RegionInfo("IL", "illinois", "US")),
            ("mich", new RegionInfo("MI", "michigan", "US")),
            ("wis", new RegionInfo("WI", "wisconsin", "US")),
            ("wisc", new RegionInfo("WI", "wisconsin", "US")),
            ("minn", new RegionInfo("MN", "minnesota", "US")),
            ("colo", new RegionInfo("CO", "colorado", "US")),
            ("ariz", new RegionInfo("AZ", "arizona", "US")),
            ("conn", new RegionInfo("CT", "connecticut", "US")),
            ("wash", new RegionInfo("WA", "washington", "US")),
            ("ore", new RegionInfo("OR", "oregon", "US")),
            ("oreg", new RegionInfo("OR", "oregon", "US")),
            ("okla", new RegionInfo("OK", "oklahoma", "US")),
            ("tex", new RegionInfo("TX", "texas", "US")),
            ("ala", new RegionInfo("AL", "alabama", "US")),
            ("ind", new RegionInfo("IN", "indiana", "US")),
            ("kans", new RegionInfo("KS", "kansas", "US")),
            ("neb", new RegionInfo("NE", "nebraska", "US")),
            ("nebr", new RegionInfo("NE", "nebraska", "US")),
            ("nev", new RegionInfo("NV", "nevada", "US")),
            ("mont", new RegionInfo("MT", "montana", "US")),
            ("wyo", new RegionInfo("WY", "wyoming", "US")),
            ("ark", new RegionInfo("AR", "arkansas", "US")),
            ("del", new RegionInfo("DE", "delaware", "US")),
            ("mass", new RegionInfo("MA", "massachusetts", "US")),
            ("penn", new RegionInfo("PA", "pennsylvania", "US")),
        ])
        .Select(r => (r.Item1, r.Item2))
        .ToList();

    // A key alone: its first country (US, then Canada, then the rest).
    private static readonly Dictionary<string, RegionInfo> RegionsByKey = RegionEntries
        .GroupBy(r => r.Key)
        .ToDictionary(g => g.Key, g => g.First().Info);

    private static readonly Dictionary<(string, string), RegionInfo> RegionsByKeyAndCountry =
        RegionEntries
            .GroupBy(r => (r.Key, r.Info.Country))
            .ToDictionary(g => g.Key, g => g.First().Info);

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
        ["ger"] = "DE",
        ["deu"] = "DE",
        ["gbr"] = "GB",
        ["nld"] = "NL",
        ["fra"] = "FR",
        ["can"] = "CA",
        ["mex"] = "MX",
        ["jpn"] = "JP",
        ["esp"] = "ES",
        ["ita"] = "IT",
        ["swe"] = "SE",
        ["dnk"] = "DK",
        ["bel"] = "BE",
        ["aut"] = "AT",
        ["irl"] = "IE",
        ["che"] = "CH",
        ["greece"] = "GR",
        ["south korea"] = "KR",
        ["korea"] = "KR",
        ["china"] = "CN",
        ["hong kong"] = "HK",
        ["taiwan"] = "TW",
        ["singapore"] = "SG",
        ["thailand"] = "TH",
        ["india"] = "IN",
        ["venezuela"] = "VE",
        ["colombia"] = "CO",
        ["chile"] = "CL",
        ["peru"] = "PE",
        ["costa rica"] = "CR",
        ["puerto rico"] = "PR",
        ["cuba"] = "CU",
        ["dominican republic"] = "DO",
        ["bahamas"] = "BS",
        ["uruguay"] = "UY",
        ["south africa"] = "ZA",
        ["morocco"] = "MA",
        ["turkey"] = "TR",
        ["russia"] = "RU",
        ["ukraine"] = "UA",
        ["hungary"] = "HU",
        ["czechia"] = "CZ",
        ["slovakia"] = "SK",
        ["slovenia"] = "SI",
        ["croatia"] = "HR",
        ["serbia"] = "RS",
        ["romania"] = "RO",
        ["bulgaria"] = "BG",
        ["estonia"] = "EE",
        ["latvia"] = "LV",
        ["lithuania"] = "LT",
        ["luxembourg"] = "LU",
        ["monaco"] = "MC",
        ["united arab emirates"] = "AE",
        ["philippines"] = "PH",
        ["indonesia"] = "ID",
        ["malaysia"] = "MY",
        ["vietnam"] = "VN",
        ["belize"] = "BZ",
        ["panama"] = "PA",
        ["ecuador"] = "EC",
        ["bolivia"] = "BO",
        // ISO codes that can't be read as a US state or Canadian province ("Paris, FR").
        ["fr"] = "FR",
        ["es"] = "ES",
        ["au"] = "AU",
        ["jp"] = "JP",
        ["it"] = "IT",
        ["se"] = "SE",
        ["dk"] = "DK",
        ["be"] = "BE",
        ["at"] = "AT",
        ["ch"] = "CH",
        ["ie"] = "IE",
        ["nz"] = "NZ",
        ["mx"] = "MX",
        ["br"] = "BR",
        ["gr"] = "GR",
        ["cz"] = "CZ",
        ["pl"] = "PL",
        ["pt"] = "PT",
        ["no"] = "NO",
        ["fi"] = "FI",
        ["hu"] = "HU",
        ["hr"] = "HR",
        ["si"] = "SI",
        ["ro"] = "RO",
        ["bg"] = "BG",
        ["rs"] = "RS",
        ["tr"] = "TR",
        ["ru"] = "RU",
        ["ua"] = "UA",
        ["ee"] = "EE",
        ["lv"] = "LV",
        ["lt"] = "LT",
        ["lu"] = "LU",
        ["jm"] = "JM",
        ["cr"] = "CR",
        ["cl"] = "CL",
        ["uy"] = "UY",
        ["ve"] = "VE",
        ["za"] = "ZA",
        ["kr"] = "KR",
        ["cn"] = "CN",
        ["hk"] = "HK",
        ["tw"] = "TW",
        ["sg"] = "SG",
        ["th"] = "TH",
        ["is"] = "IS",
    };
}
