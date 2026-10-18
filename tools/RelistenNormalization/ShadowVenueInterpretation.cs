using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A physical-place hypothesis for the shadow reader. Keys and source links are
/// proposals; none of these records accepts a canonical venue or a location history.
/// </summary>
public sealed record ShadowVenueProposal(
    string ProposedPlaceKey,
    string? DisplayName,
    string Scope,
    ShadowVenueLocality Locality,
    ShadowVenueCoordinates? Coordinates,
    IReadOnlyList<ShadowVenueHistoryName> PastNames,
    ShadowVenueProviderReference? ProviderReference,
    string GroupingBasis,
    IReadOnlyList<ShadowVenueAlternative> Alternatives,
    int AlternativeCount,
    bool AlternativesTruncated,
    IReadOnlyList<string> ProtectedUnknowns,
    IReadOnlyList<string> EnrichmentTasks,
    bool AcceptedIdentity = false
);

/// <summary>Parsed geographic wording, without claiming a gazetteer-resolved locality.</summary>
public sealed record ShadowVenueLocality(
    string? OriginalWording,
    string? City,
    string? Region,
    string? CountryCode,
    string? Address,
    string? AddressEvidenceField,
    string Basis,
    string? BrowseKey
);

/// <summary>Legacy coordinates can be city centroids; their presence is not premises evidence.</summary>
public sealed record ShadowVenueCoordinates(
    double? Latitude,
    double? Longitude,
    string Kind,
    bool ValidPair
);

/// <summary>
/// Source wording with optional year precision. Offsets use UTF-16 string indices;
/// no creation/update timestamp is repurposed as a historical validity interval.
/// </summary>
public sealed record ShadowVenueHistoryName(
    string Name,
    int? FromYear,
    int? ToYear,
    string Scope,
    string Relationship,
    string EvidenceField,
    string EvidenceQuote,
    int EvidenceOffset,
    int EvidenceLength
);

/// <summary>A provider's venue record, which may still cover an unresolved history.</summary>
public sealed record ShadowVenueProviderReference(
    string Provider,
    string ResourceType,
    string ProviderKey,
    string EvidenceField,
    string OriginalWording
);

public sealed record ShadowVenueAlternative(
    Guid InputUuid,
    long ArtistId,
    string? Name,
    string? Location,
    string Scope,
    string? Address,
    string Relationship,
    string ProposedPlaceKey,
    IdentityCandidateComparison Comparison
);

/// <summary>
/// Retains only venue fields needed by the first-pass indexes. Full native JSON
/// belongs to each output's evidence, not to this in-memory population index.
/// </summary>
internal sealed record ShadowVenueObservation(
    Guid Uuid,
    long ArtistId,
    string? Name,
    string? Location,
    string? PastNamesText,
    string? UpstreamIdentifier,
    string NameKey,
    string Scope,
    ShadowVenueLocality Locality,
    ShadowVenueCoordinates? Coordinates,
    IReadOnlyList<ShadowVenueHistoryName> PastNames,
    ShadowVenueProviderReference? ProviderReference,
    bool MultiplePlaces
)
{
    public bool HasName => NameKey.Length > 0 && !ShadowVenueText.IsUnknown(Name);

    public bool CanProposePhysicalGroup =>
        HasName
        && !MultiplePlaces
        && Locality.BrowseKey != null
        && Scope is not ("festival" or "event" or "unknown");

    public string? NameLocalityKey =>
        HasName && Locality.BrowseKey != null ? NameKey + "\n" + Locality.BrowseKey : null;

    public string? AddressGroup =>
        NameLocalityKey != null
        && ShadowVenueText.IsPremisesAddress(Locality.Address)
        && CanProposePhysicalGroup
            ? string.Join("\n", NameLocalityKey, Scope, ShadowVenueText.Key(Locality.Address))
            : null;

    public string? ProviderGroup =>
        ProviderReference is { } reference
            ? string.Join("\n", reference.Provider, reference.ResourceType, reference.ProviderKey)
            : null;

    public VenueMatchRecord MatchRecord() =>
        new(
            null,
            "",
            null,
            HasName ? [new(Name!)] : [],
            ProviderReference is { } reference
                ? [new(reference.Provider, reference.ResourceType, reference.ProviderKey)]
                : [],
            null,
            LocationAssertions: []
        );

    public bool SameMetadata(ShadowVenueObservation other) =>
        (Name, Location, PastNamesText, UpstreamIdentifier, ArtistId, Scope, Locality, Coordinates)
        == (
            other.Name,
            other.Location,
            other.PastNamesText,
            other.UpstreamIdentifier,
            other.ArtistId,
            other.Scope,
            other.Locality,
            other.Coordinates
        );

    public string ConflictingObservationKey() =>
        ShadowCatalogIdentity.Key(
            "venue",
            "conflicting_catalog_observation\n"
                + JsonSerializer.Serialize(
                    new
                    {
                        Uuid,
                        ArtistId,
                        Name,
                        Location,
                        PastNamesText,
                        UpstreamIdentifier,
                        Scope,
                        Locality,
                        Coordinates,
                    }
                )
        );

    public static ShadowVenueObservation Read(JsonElement row)
    {
        var name = ShadowVenueText.Text(row, "name");
        var location = ShadowVenueText.Text(row, "location");
        var past = ShadowVenueText.Text(row, "past_names");
        var upstream = ShadowVenueText.Text(row, "upstream_identifier");
        var multiple =
            ShadowVenueText.MultiplePlaces(name) || ShadowVenueText.MultiplePlaces(location);
        return new(
            row.GetProperty("uuid").GetGuid(),
            row.TryGetProperty("artist_id", out var artist) && artist.TryGetInt64(out var id)
                ? id
                : 0,
            name,
            location,
            past,
            upstream,
            ShadowVenueText.Key(name),
            ShadowVenueText.ExplicitScope(row, name),
            ShadowVenueText.Locality(row, name, location, multiple),
            ShadowVenueText.Coordinates(row),
            ShadowVenueText.History(past),
            ShadowVenueText.ProviderReference(upstream),
            multiple
        );
    }
}

/// <summary>
/// Local parsers make explicit, reversible interpretations. City labels remain
/// labels: only the later geography resolver may supply a resolved locality ID.
/// </summary>
internal static class ShadowVenueText
{
    private static readonly Regex Separators = new(
        @"[^\p{L}\p{N}\p{M}]+",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex Street = new(
        @"\b\d{1,6}[A-Za-z]?\s+(?:[\p{L}\p{N}.'’-]+\s+){1,6}(?:Street|St\.?|Road|Rd\.?|Avenue|Ave\.?|Boulevard|Blvd\.?|Lane|Ln\.?|Drive|Dr\.?|Way|Court|Ct\.?)\b(?:\s+(?:Apt\.?|Suite|Unit)\s*#?\s*[\p{L}\p{N}-]+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex HistorySegments = new(
        @"[^;\r\n]+",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex YearRange = new(
        @"^(?<name>.+?)\s*\((?<from>[0-9]{4})\s*[-–—]\s*(?<to>[0-9]{4})\)\s*$",
        RegexOptions.CultureInvariant
    );
    private static readonly IReadOnlyDictionary<string, string> UsRegions = Regions(
        "Alabama:AL|Alaska:AK|Arizona:AZ|Arkansas:AR|California:CA|Colorado:CO|Connecticut:CT|Delaware:DE|District of Columbia:DC|Florida:FL|Georgia:GA|Hawaii:HI|Idaho:ID|Illinois:IL|Indiana:IN|Iowa:IA|Kansas:KS|Kentucky:KY|Louisiana:LA|Maine:ME|Maryland:MD|Massachusetts:MA|Michigan:MI|Minnesota:MN|Mississippi:MS|Missouri:MO|Montana:MT|Nebraska:NE|Nevada:NV|New Hampshire:NH|New Jersey:NJ|New Mexico:NM|New York:NY|North Carolina:NC|North Dakota:ND|Ohio:OH|Oklahoma:OK|Oregon:OR|Pennsylvania:PA|Rhode Island:RI|South Carolina:SC|South Dakota:SD|Tennessee:TN|Texas:TX|Utah:UT|Vermont:VT|Virginia:VA|Washington:WA|West Virginia:WV|Wisconsin:WI|Wyoming:WY"
    );
    private static readonly IReadOnlyDictionary<string, string> CanadianRegions = Regions(
        "Alberta:AB|British Columbia:BC|Manitoba:MB|New Brunswick:NB|Newfoundland and Labrador:NL|Northwest Territories:NT|Nova Scotia:NS|Nunavut:NU|Ontario:ON|Prince Edward Island:PE|Quebec:QC|Québec:QC|Saskatchewan:SK|Yukon:YT"
    );
    private static readonly IReadOnlyDictionary<string, string> Countries = CountryNames();

    public static string Key(string? text) =>
        text == null
            ? ""
            : Separators
                .Replace(text.Normalize(NormalizationForm.FormC).ToLowerInvariant(), " ")
                .Trim();

    public static string? Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool IsUnknown(string? text) =>
        Key(text)
            is ""
                or "unknown"
                or "unknown venue"
                or "unknown location"
                or "n a"
                or "na"
                or "tba"
                or "none"
                or "not specified"
                or "blank coverage";

    public static bool MultiplePlaces(string? text) =>
        text != null
        && (
            text.Contains(" / ", StringComparison.Ordinal)
            || CompoundLocalities(text)
            || text.Contains("multiple venues", StringComparison.OrdinalIgnoreCase)
            || text.Contains("various venues", StringComparison.OrdinalIgnoreCase)
        );

    private static bool CompoundLocalities(string text)
    {
        var separator = text.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
        if (
            separator < 0
            || !text[..separator].Contains(',')
            || !text[(separator + 5)..].Contains(',')
        )
        {
            return false;
        }
        var fieldStart = text.LastIndexOf(',', separator);
        var fieldEnd = text.IndexOf(',', separator + 5);
        var field = Key(text[(fieldStart + 1)..fieldEnd]);
        return !UsRegions.ContainsKey(field) && !CanadianRegions.ContainsKey(field);
    }

    public static string Scope(string? name)
    {
        var key = Key(name);
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (words.Contains("stage"))
        {
            return "stage";
        }
        if (
            words.Contains("room")
            || key.Contains("theatre at ", StringComparison.Ordinal)
            || key.Contains("theater at ", StringComparison.Ordinal)
        )
        {
            return "room";
        }
        if (words.Overlaps(["festival", "fest", "campout"]))
        {
            return "festival";
        }
        if (words.Overlaps(["wedding", "birthday", "fundraiser", "benefit"]))
        {
            return "event";
        }
        return IsUnknown(name) ? "unknown" : "place";
    }

    public static string ExplicitScope(JsonElement row, string? name)
    {
        var explicitKind = Text(row, "venue_kind") ?? Text(row, "kind");
        return explicitKind is "room" or "stage" or "site" or "building" or "festival" or "event"
            ? explicitKind
            : Scope(name);
    }

    public static ShadowVenueLocality Locality(
        JsonElement row,
        string? name,
        string? location,
        bool multiple
    )
    {
        var address = Text(row, "address") ?? Text(row, "street_address");
        var addressField =
            address == null ? null
            : Text(row, "address") != null ? "address"
            : "street_address";
        var city = Text(row, "city");
        var region = Text(row, "region") ?? Text(row, "state");
        var country = Country(Text(row, "country_code") ?? Text(row, "country"));
        var geographyConflict = false;
        var basis = city == null ? "parsed_catalog_location" : "explicit_catalog_geography";
        string[] parts = IsUnknown(location)
            ? []
            : location!.Split(',').Select(part => part.Trim()).ToArray();
        if (parts.Length > 1 && Street.IsMatch(parts[0]))
        {
            if (address == null)
            {
                address = Street.Match(parts[0]).Value;
                addressField = "location";
            }
            parts = parts[1..];
        }
        if (address == null && name != null && Street.Match(name) is { Success: true } match)
        {
            address = match.Value;
            addressField = "name";
        }
        if (city == null && !multiple && parts.Length > 0)
        {
            city = parts.Length == 1 && Country(parts[0]) != null ? null : parts[0];
            if (city == null)
            {
                country ??= Country(parts[0]);
            }
            if (parts.Length >= 3)
            {
                region ??= string.IsNullOrWhiteSpace(parts[^2]) ? null : parts[^2];
                country ??= Country(parts[^1]);
            }
            else if (parts.Length == 2)
            {
                if (
                    UsRegions.ContainsKey(Key(parts[1]))
                    || CanadianRegions.ContainsKey(Key(parts[1]))
                )
                {
                    region ??= parts[1];
                }
                else
                {
                    country ??= Country(parts[1]);
                }
            }
        }
        if (
            region != null
            && UsRegions.TryGetValue(Key(region), out var state)
            && country is null or "US"
        )
        {
            region = state;
            country = "US";
        }
        else if (
            region != null
            && CanadianRegions.TryGetValue(Key(region), out var province)
            && country is null or "CA"
        )
        {
            region = province;
            country = "CA";
        }
        else if (
            region != null
            && (UsRegions.ContainsKey(Key(region)) || CanadianRegions.ContainsKey(Key(region)))
        )
        {
            geographyConflict = true;
        }
        city = IsUnknown(city) || multiple ? null : city?.Trim();
        var browse =
            city != null && country != null && !geographyConflict
                ? ShadowCatalogIdentity.Key(
                    "venue_locality",
                    JsonSerializer.Serialize(new[] { country, Key(region), Key(city) })
                )
                : null;
        return new(
            location,
            city,
            region,
            country,
            address,
            addressField,
            geographyConflict ? "conflicting_catalog_geography"
                : browse == null ? "unresolved_locality"
                : basis,
            browse
        );
    }

    public static bool IsPremisesAddress(string? address) =>
        address != null && Street.IsMatch(address);

    public static ShadowVenueCoordinates? Coordinates(JsonElement row)
    {
        double? Number(string field) =>
            row.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
                ? number
                : null;
        var latitude = Number("latitude");
        var longitude = Number("longitude");
        return latitude == null && longitude == null
            ? null
            : new(
                latitude,
                longitude,
                "unknown",
                latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180
            );
    }

    public static IReadOnlyList<ShadowVenueHistoryName> History(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var result = new List<ShadowVenueHistoryName>();
        foreach (Match segment in HistorySegments.Matches(text))
        {
            var quote = segment.Value.Trim();
            if (quote.Length == 0)
            {
                continue;
            }
            var offset = segment.Index + segment.Value.IndexOf(quote, StringComparison.Ordinal);
            var range = YearRange.Match(quote);
            var from = range.Success
                ? int.Parse(range.Groups["from"].Value, CultureInfo.InvariantCulture)
                : (int?)null;
            var to = range.Success
                ? int.Parse(range.Groups["to"].Value, CultureInfo.InvariantCulture)
                : (int?)null;
            if (from is <= 0 || to is <= 0 || from > to)
            {
                from = null;
                to = null;
            }
            var name = range.Success ? range.Groups["name"].Value.Trim() : quote;
            result.Add(
                new(
                    name,
                    from,
                    to,
                    Scope(name),
                    "provider_past_name_unverified_premises",
                    "past_names",
                    quote,
                    offset,
                    quote.Length
                )
            );
        }
        return result;
    }

    public static ShadowVenueProviderReference? ProviderReference(string? value)
    {
        if (value == null)
        {
            return null;
        }
        var colon = value.IndexOf(':');
        if (colon < 1)
        {
            return null;
        }
        var provider = value[..colon].ToLowerInvariant();
        var key = value[(colon + 1)..];
        if (
            provider == "setlistfm"
            && Regex.IsMatch(key, @"\A[0-9a-fA-F]{8}\z", RegexOptions.CultureInvariant)
        )
        {
            return new("setlistfm", "venue", key.ToLowerInvariant(), "upstream_identifier", value);
        }
        if (provider == "pt" && key.Length > 0 && key.All(char.IsAsciiDigit))
        {
            return new("phantasytour", "venue", key, "upstream_identifier", value);
        }
        return null;
    }

    private static string? Country(string? text) =>
        text != null && Countries.TryGetValue(Key(text), out var country) ? country : null;

    private static IReadOnlyDictionary<string, string> Regions(string values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values.Split('|'))
        {
            var parts = item.Split(':');
            result[Key(parts[0])] = parts[1];
            result[Key(parts[1])] = parts[1];
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> CountryNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var region = new RegionInfo(culture.Name);
            result[Key(region.EnglishName)] = region.TwoLetterISORegionName;
            result[Key(region.TwoLetterISORegionName)] = region.TwoLetterISORegionName;
            result[Key(region.ThreeLetterISORegionName)] = region.TwoLetterISORegionName;
        }
        foreach (var alias in new[] { "USA", "U.S.A.", "U.S.", "United States of America" })
        {
            result[Key(alias)] = "US";
        }
        foreach (var alias in new[] { "UK", "England", "Scotland", "Wales", "Northern Ireland" })
        {
            result[Key(alias)] = "GB";
        }
        return result;
    }
}
