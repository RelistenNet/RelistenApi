using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Relisten.Import;

public sealed class ArchiveOrgVenueInferenceResult
{
    public string? ProposedVenue { get; init; }
    public string? ProposedCoverage { get; init; }
    public string Confidence { get; init; } = "low";
    public string Notes { get; init; } = "";
    public IReadOnlyList<string> DescriptionFirstLines { get; init; } = Array.Empty<string>();
}

public sealed class ArchiveOrgResolvedVenue
{
    public string VenueName { get; init; } = "";
    public string Coverage { get; init; } = "";
    public ArchiveOrgVenueInferenceResult Inference { get; init; } = new();
}

public static class ArchiveOrgVenueInference
{
    private const string StatePattern =
        "AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|IA|ID|IL|IN|KS|KY|LA|MA|MD|ME|MI|MN|MO|MS|MT|NC|ND|NE|NH|NJ|NM|NV|NY|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VA|VT|WA|WI|WV|WY|DC|Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|Delaware|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming|England|UK";

    private static readonly Regex DescriptionLineBreakTags =
        new(@"</?(div|p)[^>]*>|<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RemainingHtmlTags =
        new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex Whitespace =
        new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex OrdinalDay =
        new(@"\b(\d{1,2})(st|nd|rd|th)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsoDate =
        new(@"\b\d{4}-\d{1,2}-\d{1,2}\b", RegexOptions.Compiled);

    private static readonly Regex MonthNameDate =
        new(@"\b(?:jan(?:uary)?|feb(?:r?uary|urary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b.*\b\d{4}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonthName =
        new(@"\b(?:jan(?:uary)?|feb(?:r?uary|urary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DayMonthNameDate =
        new(@"\b\d{1,2}\b.*\b(?:jan(?:uary)?|feb(?:r?uary|urary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b.*\b\d{4}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericDate =
        new(@"^\d{1,2}/\d{1,2}/\d{2,4}$", RegexOptions.Compiled);

    private static readonly Regex FourDigitYear =
        new(@"\b(?:19|20)\d{2}\b", RegexOptions.Compiled);

    private static readonly Regex AddressLine =
        new(@"^\d{2,6}\s+.*\b(?:ave|avenue|st|street|road|rd|blvd|boulevard|drive|dr|lane|ln|place|pl|parkway|pkwy|way|wacker|morse)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StreetSegmentLine =
        new(@"\bbetween\b.*\b(?:ave|avenue|st|street|road|rd|blvd|boulevard|drive|dr|lane|ln|place|pl|way)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocationWithComma =
        new(@"^[\p{L} .'\-]+,\s*(?:" + StatePattern + @")(?:\s+\d{5}(?:-\d{4})?)?(?:,?\s*(?:USA|United States))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocationWithoutComma =
        new(@"^[\p{L} .'\-]+\s+(?:" + StatePattern + @")(?:\s+\d{5}(?:-\d{4})?)?(?:,?\s*(?:USA|United States))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrackListLine =
        new(@"^(?:\d{1,3}[.)]?\s+|encore\b|openers:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CityAtVenue =
        new(@"^(?<coverage>.+?)\s+at\s+(?<article>the\s+)?(?<venue>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CityStateInLine =
        new(@"\b(?<city>[\p{L} .'\-]+?)(?:,\s*|\s+)(?<state>" + StatePattern + @")(?:\s+\d{5}(?:-\d{4})?)?(?:,?\s*(?:USA|United States))?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VenueKnownCityWithComma =
        new(@"^(?<venue>.+?),\s*(?<city>Chicago|Berwyn|Evanston|Milwaukee|Madison|London|Leeds|New York)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VenueKnownCityStateWithComma =
        new(@"^(?<venue>.+?),\s*(?<city>Chicago|Berwyn|Evanston|Milwaukee|Madison|London|Leeds|New York),?\s*(?<state>IL|WI|NY|UK|England|Illinois|Wisconsin)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VenueKnownCityStateWithoutComma =
        new(@"^(?<venue>.+?)\s+(?<city>Chicago|Berwyn|Evanston|Milwaukee|Madison|London|Leeds|New York)\s+(?<state>IL|WI|NY|UK|England|Illinois|Wisconsin)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KnownCityCoverage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chicago"] = "Chicago, IL",
        ["Berwyn"] = "Berwyn, IL",
        ["Evanston"] = "Evanston, IL",
        ["Milwaukee"] = "Milwaukee, WI",
        ["Madison"] = "Madison, WI",
        ["London"] = "London, UK",
        ["Leeds"] = "Leeds, UK",
        ["New York"] = "New York, NY"
    };

    private static readonly Dictionary<string, string> StateAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"] = "AL",
        ["Alaska"] = "AK",
        ["Arizona"] = "AZ",
        ["Arkansas"] = "AR",
        ["California"] = "CA",
        ["Colorado"] = "CO",
        ["Connecticut"] = "CT",
        ["Delaware"] = "DE",
        ["Florida"] = "FL",
        ["Georgia"] = "GA",
        ["Hawaii"] = "HI",
        ["Idaho"] = "ID",
        ["Illinois"] = "IL",
        ["Indiana"] = "IN",
        ["Iowa"] = "IA",
        ["Kansas"] = "KS",
        ["Kentucky"] = "KY",
        ["Louisiana"] = "LA",
        ["Maine"] = "ME",
        ["Maryland"] = "MD",
        ["Massachusetts"] = "MA",
        ["Michigan"] = "MI",
        ["Minnesota"] = "MN",
        ["Mississippi"] = "MS",
        ["Missouri"] = "MO",
        ["Montana"] = "MT",
        ["Nebraska"] = "NE",
        ["Nevada"] = "NV",
        ["New Hampshire"] = "NH",
        ["New Jersey"] = "NJ",
        ["New Mexico"] = "NM",
        ["New York"] = "NY",
        ["North Carolina"] = "NC",
        ["North Dakota"] = "ND",
        ["Ohio"] = "OH",
        ["Oklahoma"] = "OK",
        ["Oregon"] = "OR",
        ["Pennsylvania"] = "PA",
        ["Rhode Island"] = "RI",
        ["South Carolina"] = "SC",
        ["South Dakota"] = "SD",
        ["Tennessee"] = "TN",
        ["Texas"] = "TX",
        ["Utah"] = "UT",
        ["Vermont"] = "VT",
        ["Virginia"] = "VA",
        ["Washington"] = "WA",
        ["West Virginia"] = "WV",
        ["Wisconsin"] = "WI",
        ["Wyoming"] = "WY",
        ["England"] = "UK"
    };

    private static readonly string[] StopPrefixes =
    [
        "recording generously loaned",
        "master recording generously loaned",
        "source:",
        "transfer:",
        "transfer by:",
        "transferred by:",
        "mastering:",
        "mastered by:",
        "recorded by:",
        "recorded by ",
        "audience recording",
        "master cassette",
        "raw wav file",
        "setlist:",
        "setlist",
        "taper:",
        "notes:",
        "note:",
        "** 16 bit",
        "** 24 bit"
    ];

    public static ArchiveOrgResolvedVenue ResolveVenue(
        string? metadataVenue,
        string? metadataCoverage,
        string? descriptionHtml,
        string? artistName,
        string? title,
        string? displayDate,
        bool inferFromDescription)
    {
        var venue = metadataVenue ?? "";
        var coverage = metadataCoverage ?? "";

        if (!inferFromDescription)
        {
            return new ArchiveOrgResolvedVenue
            {
                VenueName = venue,
                Coverage = coverage,
                Inference = new ArchiveOrgVenueInferenceResult
                {
                    Confidence = "disabled",
                    Notes = "inference disabled"
                }
            };
        }

        var inference = Infer(descriptionHtml, artistName, title, displayDate, metadataVenue, metadataCoverage);

        if (!HasKnownVenue(venue) && !string.IsNullOrWhiteSpace(inference.ProposedVenue))
        {
            venue = inference.ProposedVenue;
        }

        if (HasKnownCoverage(coverage) && TryNormalizeCoverage(coverage, out var normalizedMetadataCoverage))
        {
            coverage = normalizedMetadataCoverage;
        }
        else if (!HasKnownCoverage(coverage) && !string.IsNullOrWhiteSpace(inference.ProposedCoverage))
        {
            coverage = inference.ProposedCoverage;
        }

        return new ArchiveOrgResolvedVenue
        {
            VenueName = venue,
            Coverage = coverage ?? "",
            Inference = inference
        };
    }

    public static ArchiveOrgVenueInferenceResult Infer(
        string? descriptionHtml,
        string? artistName,
        string? title,
        string? displayDate,
        string? currentVenue = null,
        string? currentCoverage = null)
    {
        var lines = ExtractDescriptionLines(descriptionHtml);
        var headerLines = HeaderLines(lines, artistName, title, displayDate).ToList();
        var notes = new List<string>();
        var hasCurrentVenue = TryUseCurrentVenue(currentVenue, out var currentVenueValue);
        var hasCurrentCoverage = TryUseCurrentCoverage(currentCoverage, out var currentCoverageValue);

        if (hasCurrentVenue)
        {
            notes.Add("current venue used");
        }

        if (hasCurrentCoverage)
        {
            notes.Add("current coverage used");
        }

        var compact = TryInferFromCompactHeader(headerLines, artistName, title, displayDate, notes);
        if (compact != null)
        {
            return BuildResult(ApplyCurrentValues(compact, currentVenueValue, currentCoverageValue), notes, lines,
                hasCurrentVenue || hasCurrentCoverage);
        }

        var coverageStartIndex = -1;
        var coverageEndIndex = -1;
        string? proposedCoverage = null;
        for (var i = headerLines.Count - 1; i >= 0; i--)
        {
            if (TryCoverageAt(headerLines, i, out proposedCoverage, out coverageStartIndex, out coverageEndIndex))
            {
                break;
            }
        }

        if (coverageStartIndex < 0)
        {
            notes.Add("no coverage line found");
        }

        var venueCandidates = coverageStartIndex >= 0
            ? headerLines.Take(coverageStartIndex)
            : headerLines;

        var filteredVenueCandidates = FilterVenueCandidates(venueCandidates, artistName, title, displayDate, notes);

        string? proposedVenue = null;
        if (filteredVenueCandidates.Count > 0)
        {
            if (coverageStartIndex < 0 && filteredVenueCandidates.Count > 3)
            {
                notes.Add("too many header lines without coverage");
                filteredVenueCandidates.Clear();
            }
        }

        if (filteredVenueCandidates.Count == 0 && coverageEndIndex >= 0)
        {
            var followingVenueCandidates = FilterVenueCandidates(headerLines.Skip(coverageEndIndex + 1), artistName,
                title, displayDate, notes);
            if (followingVenueCandidates.Count > 0)
            {
                filteredVenueCandidates = followingVenueCandidates.Take(1).ToList();
                notes.Add("venue follows coverage");
            }
        }

        if (filteredVenueCandidates.Count > 0)
        {
            proposedVenue = string.Join(" - ", filteredVenueCandidates);
            notes.Add(filteredVenueCandidates.Count == 1 ? "single-line venue" : "multi-line venue");
        }
        else
        {
            notes.Add("no venue line found");
        }

        var parsed = new ParsedVenueCoverage(proposedVenue, proposedCoverage,
            filteredVenueCandidates.Count > 1 || notes.Contains("venue follows coverage"));
        return BuildResult(ApplyCurrentValues(parsed, currentVenueValue, currentCoverageValue), notes, lines,
            hasCurrentVenue || hasCurrentCoverage);
    }

    private static IReadOnlyList<string> ExtractDescriptionLines(string? descriptionHtml)
    {
        if (string.IsNullOrWhiteSpace(descriptionHtml))
        {
            return Array.Empty<string>();
        }

        var withLineBreaks = DescriptionLineBreakTags.Replace(descriptionHtml, "\n");
        var withoutTags = RemainingHtmlTags.Replace(withLineBreaks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace('\u00a0', ' ');

        return decoded
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static List<string> FilterVenueCandidates(IEnumerable<string> venueCandidates, string? artistName,
        string? title, string? displayDate, List<string> notes)
    {
        return venueCandidates
            .Where(line => !IsIgnoredHeaderLine(line, artistName, title, displayDate))
            .Where(line =>
            {
                if (!IsAddressLine(line))
                {
                    return true;
                }

                notes.Add("address line omitted");
                return false;
            })
            .Where(line => !IsCountryLine(line))
            .Where(line => !IsTrackLine(line))
            .ToList();
    }

    private static IEnumerable<string> HeaderLines(IReadOnlyList<string> lines, string? artistName, string? title,
        string? displayDate)
    {
        foreach (var line in lines)
        {
            if (IsStopLine(line) && !IsIgnoredHeaderLine(line, artistName, title, displayDate))
            {
                yield break;
            }

            yield return line;
        }
    }

    private static bool IsStopLine(string line)
    {
        var normalized = line.Trim().ToLowerInvariant();
        return StopPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
               || IsTrackLine(line);
    }

    private static bool TryUseCurrentVenue(string? currentVenue, out string? venue)
    {
        venue = null;
        if (!HasKnownVenue(currentVenue))
        {
            return false;
        }

        venue = Normalize(currentVenue!);
        return true;
    }

    private static bool TryUseCurrentCoverage(string? currentCoverage, out string? coverage)
    {
        coverage = null;
        if (!HasKnownCoverage(currentCoverage))
        {
            return false;
        }

        coverage = TryNormalizeCoverage(currentCoverage!, out var normalizedCoverage)
            ? normalizedCoverage
            : Normalize(currentCoverage!);
        return true;
    }

    private static bool HasKnownVenue(string? venue)
    {
        return !string.IsNullOrWhiteSpace(venue) &&
               !Normalize(venue).Equals("Unknown Venue", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasKnownCoverage(string? coverage)
    {
        return !string.IsNullOrWhiteSpace(coverage) &&
               !Normalize(coverage).Equals("Unknown Location", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedVenueCoverage ApplyCurrentValues(ParsedVenueCoverage parsed, string? currentVenue,
        string? currentCoverage)
    {
        return parsed with
        {
            Venue = currentVenue ?? parsed.Venue,
            Coverage = currentCoverage ?? parsed.Coverage
        };
    }

    private static bool IsIgnoredHeaderLine(string line, string? artistName, string? title, string? displayDate)
    {
        return IsArtistLine(line, artistName)
               || IsTitleLine(line, title)
               || IsDateLine(line, displayDate)
               || IsLocationLine(line);
    }

    private static bool IsArtistLine(string line, string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return false;
        }

        if (NormalizeIdentity(line) == NormalizeIdentity(artistName))
        {
            return true;
        }

        var normalizedLine = Normalize(line);
        var normalizedArtist = Normalize(artistName);
        if (!normalizedLine.StartsWith(normalizedArtist, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalizedLine[normalizedArtist.Length..].TrimStart();
        return remainder.StartsWith('(');
    }

    private static bool IsTitleLine(string line, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (NormalizeIdentity(line) == NormalizeIdentity(title))
        {
            return true;
        }

        var normalizedLine = Normalize(line);
        var normalizedTitle = Normalize(title);
        return normalizedTitle.StartsWith($"{normalizedLine} - ", StringComparison.OrdinalIgnoreCase) ||
               normalizedTitle.StartsWith($"{normalizedLine} Live at ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDateLine(string line, string? displayDate)
    {
        var normalized = OrdinalDay.Replace(line, "$1");

        if (IsoDate.IsMatch(normalized) || MonthNameDate.IsMatch(normalized) ||
            DayMonthNameDate.IsMatch(normalized) || NumericDate.IsMatch(normalized))
        {
            return true;
        }

        if (LooksLikeDateText(normalized) &&
            DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
        {
            return true;
        }

        if (!DateTime.TryParse(displayDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDisplayDate))
        {
            return false;
        }

        return LooksLikeDateText(normalized) &&
               DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces,
                   out var parsedLine)
               && parsedLine.Date == parsedDisplayDate.Date;
    }

    private static bool LooksLikeDateText(string value)
    {
        return FourDigitYear.IsMatch(value) || MonthName.IsMatch(value) || value.Contains('/');
    }

    private static bool IsLocationLine(string line)
    {
        return LocationWithComma.IsMatch(line)
               || LocationWithoutComma.IsMatch(line)
               || KnownCityCoverage.ContainsKey(Normalize(line));
    }

    private static bool IsAddressLine(string line)
    {
        return AddressLine.IsMatch(line) || StreetSegmentLine.IsMatch(line);
    }

    private static bool IsCountryLine(string line)
    {
        return line.Equals("USA", StringComparison.OrdinalIgnoreCase)
               || line.Equals("United States", StringComparison.OrdinalIgnoreCase)
               || line.Equals("United States of America", StringComparison.OrdinalIgnoreCase)
               || line.Equals("UK", StringComparison.OrdinalIgnoreCase)
               || line.Equals("England", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrackLine(string line)
    {
        return TrackListLine.IsMatch(line);
    }

    private static ParsedVenueCoverage? TryInferFromCompactHeader(IReadOnlyList<string> headerLines,
        string? artistName, string? title, string? displayDate, List<string> notes)
    {
        foreach (var line in headerLines.Take(6))
        {
            if (IsArtistLine(line, artistName) || IsTitleLine(line, title) || IsLocationLine(line) ||
                IsAddressLine(line) ||
                IsCountryLine(line) || IsTrackLine(line))
            {
                continue;
            }

            if (TryParseCityAtVenue(line, out var parsedCityAtVenue))
            {
                notes.Add("compact city-at-venue line");
                return parsedCityAtVenue;
            }

            var compactLine = RemoveCompactHeaderPrefix(line, artistName);
            if (TryParseVenueCoverageLine(compactLine, out var parsedVenueCoverage))
            {
                notes.Add("compact venue/location line");
                return parsedVenueCoverage;
            }
        }

        if (TryInferFromCityCountryStack(headerLines, artistName, title, displayDate, notes, out var stacked))
        {
            return stacked;
        }

        return null;
    }

    private static bool TryParseCityAtVenue(string line, out ParsedVenueCoverage? parsed)
    {
        parsed = null;
        var match = CityAtVenue.Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!TryNormalizeCoverage(match.Groups["coverage"].Value, out var coverage))
        {
            return false;
        }

        var venue = CleanVenue(match.Groups["venue"].Value);
        if (match.Groups["article"].Success &&
            venue.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            venue = venue[4..];
        }

        if (string.IsNullOrWhiteSpace(venue))
        {
            return false;
        }

        parsed = new ParsedVenueCoverage(venue, coverage, false);
        return true;
    }

    private static bool TryParseVenueCoverageLine(string line, out ParsedVenueCoverage? parsed)
    {
        parsed = null;
        var normalized = Normalize(line);

        var venueKnownCityStateWithCommaMatch = VenueKnownCityStateWithComma.Match(normalized);
        if (venueKnownCityStateWithCommaMatch.Success &&
            TryNormalizeCoverage(
                $"{venueKnownCityStateWithCommaMatch.Groups["city"].Value} {venueKnownCityStateWithCommaMatch.Groups["state"].Value}",
                out var knownCityStateWithCommaCoverage))
        {
            parsed = new ParsedVenueCoverage(CleanVenue(venueKnownCityStateWithCommaMatch.Groups["venue"].Value),
                knownCityStateWithCommaCoverage, false);
            return true;
        }

        var cityStateMatch = CityStateInLine.Matches(normalized)
            .Cast<Match>()
            .LastOrDefault();
        if (cityStateMatch != null && cityStateMatch.Success)
        {
            var coverageText = cityStateMatch.Value;
            if (TryNormalizeCoverage(coverageText, out var coverage))
            {
                var venue = CleanVenue(normalized[..cityStateMatch.Index]);
                if (!string.IsNullOrWhiteSpace(venue))
                {
                    parsed = new ParsedVenueCoverage(venue, coverage, false);
                    return true;
                }
            }
        }

        var venueKnownCityMatch = VenueKnownCityWithComma.Match(normalized);
        if (venueKnownCityMatch.Success &&
            TryNormalizeCoverage(venueKnownCityMatch.Groups["city"].Value, out var knownCityCoverage))
        {
            parsed = new ParsedVenueCoverage(CleanVenue(venueKnownCityMatch.Groups["venue"].Value),
                knownCityCoverage, false);
            return true;
        }

        var venueKnownCityStateMatch = VenueKnownCityStateWithoutComma.Match(normalized);
        if (venueKnownCityStateMatch.Success &&
            TryNormalizeCoverage($"{venueKnownCityStateMatch.Groups["city"].Value} {venueKnownCityStateMatch.Groups["state"].Value}",
                out var knownCityStateCoverage))
        {
            parsed = new ParsedVenueCoverage(CleanVenue(venueKnownCityStateMatch.Groups["venue"].Value),
                knownCityStateCoverage, false);
            return true;
        }

        return false;
    }

    private static bool TryInferFromCityCountryStack(IReadOnlyList<string> headerLines, string? artistName,
        string? title, string? displayDate, List<string> notes, out ParsedVenueCoverage? parsed)
    {
        parsed = null;

        for (var i = headerLines.Count - 1; i >= 0; i--)
        {
            string? coverage = null;
            var coverageStart = -1;

            if (i > 0 && IsCountryLine(headerLines[i]) &&
                TryNormalizeCoverage(headerLines[i - 1], out var previousLineCoverage))
            {
                coverage = previousLineCoverage;
                coverageStart = i - 1;
            }
            else if (i > 0 && IsCountryLine(headerLines[i]) &&
                     TryNormalizeCoverage($"{headerLines[i - 1]}, {headerLines[i]}", out var cityCountryCoverage))
            {
                coverage = cityCountryCoverage;
                coverageStart = i - 1;
            }
            else if (TryNormalizeCoverage(headerLines[i], out var lineCoverage) &&
                     KnownCityCoverage.ContainsKey(Normalize(headerLines[i])))
            {
                coverage = lineCoverage;
                coverageStart = i;
            }

            if (coverage == null || coverageStart < 0)
            {
                continue;
            }

            var venueCandidates = new List<string>();
            foreach (var line in headerLines.Take(coverageStart))
            {
                if (IsIgnoredHeaderLine(line, artistName, title, displayDate) ||
                    IsCountryLine(line) ||
                    IsTrackLine(line))
                {
                    continue;
                }

                if (IsAddressLine(line))
                {
                    notes.Add("address line omitted");
                    continue;
                }

                venueCandidates.Add(line);
            }

            if (venueCandidates.Count == 0)
            {
                continue;
            }

            notes.Add(venueCandidates.Count == 1 ? "single-line venue" : "multi-line venue");
            parsed = new ParsedVenueCoverage(string.Join(" - ", venueCandidates), coverage,
                venueCandidates.Count > 1);
            return true;
        }

        return false;
    }

    private static bool TryCoverageAt(IReadOnlyList<string> lines, int index, out string? coverage,
        out int coverageStartIndex, out int coverageEndIndex)
    {
        coverage = null;
        coverageStartIndex = -1;
        coverageEndIndex = -1;

        if (index > 0 && IsCountryLine(lines[index]) &&
            TryNormalizeCoverage(lines[index - 1], out var previousLineCoverage))
        {
            coverage = previousLineCoverage;
            coverageStartIndex = index - 1;
            coverageEndIndex = index;
            return true;
        }

        if (index > 0 && IsCountryLine(lines[index]) &&
            TryNormalizeCoverage($"{lines[index - 1]}, {lines[index]}", out var countryCoverage))
        {
            coverage = countryCoverage;
            coverageStartIndex = index - 1;
            coverageEndIndex = index;
            return true;
        }

        if (TryNormalizeCoverage(lines[index], out var lineCoverage))
        {
            coverage = lineCoverage;
            coverageStartIndex = index;
            coverageEndIndex = index;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeCoverage(string value, out string? coverage)
    {
        coverage = null;
        var normalized = Normalize(value)
            .Trim(' ', ',', ';')
            .Replace(". IL", ", IL", StringComparison.OrdinalIgnoreCase);

        normalized = Regex.Replace(normalized, @"\s+\([^)]*\)$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*,?\s*(USA|United States|United States of America)$", "",
            RegexOptions.IgnoreCase).Trim(' ', ',');

        if (KnownCityCoverage.TryGetValue(normalized, out var knownCoverage))
        {
            coverage = knownCoverage;
            return true;
        }

        var match = Regex.Match(normalized,
            @"^(?<city>[\p{L} .'\-]+?)(?:,\s*|\s+)(?<state>" + StatePattern + @")(?<zip>\s+\d{5}(?:-\d{4})?)?$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var city = Normalize(match.Groups["city"].Value).Trim(' ', ',');
        var state = NormalizeState(match.Groups["state"].Value);
        coverage = $"{city}, {state}";
        return true;
    }

    private static string NormalizeState(string state)
    {
        return StateAbbreviations.TryGetValue(state, out var abbreviation)
            ? abbreviation
            : state.Length == 2
                ? state.ToUpperInvariant()
                : state;
    }

    private static string RemoveCompactHeaderPrefix(string line, string? artistName)
    {
        var normalized = Normalize(line.Replace('—', '-').Replace('–', '-'));

        if (!string.IsNullOrWhiteSpace(artistName) &&
            normalized.StartsWith(artistName, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[artistName.Length..].TrimStart(' ', '-', ':');
        }

        normalized = Regex.Replace(normalized, @"^\d{4}-\d{1,2}-\d{1,2}\s+", "");
        normalized = Regex.Replace(normalized,
            @"^(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+\d{1,2},?\s+\d{4}\s+",
            "", RegexOptions.IgnoreCase);

        return normalized;
    }

    private static string CleanVenue(string venue)
    {
        var cleaned = Normalize(venue)
            .Trim(' ', ',', '-', '–', '—', ':', ';');

        cleaned = Regex.Replace(cleaned, @"\s+\d{4}-\d{1,2}-\d{1,2}\s+", " ");
        cleaned = Regex.Replace(cleaned, @"\b\d{4}-\d{1,2}-\d{1,2}\b", "");
        return Normalize(cleaned).Trim(' ', ',', '-', '–', '—', ':', ';');
    }

    private static ArchiveOrgVenueInferenceResult BuildResult(ParsedVenueCoverage parsed, List<string> notes,
        IReadOnlyList<string> lines, bool usedCurrentValue = false)
    {
        var confidence = "low";
        if (!string.IsNullOrWhiteSpace(parsed.Venue) && !string.IsNullOrWhiteSpace(parsed.Coverage))
        {
            confidence = usedCurrentValue
                ? "existing"
                : parsed.IsMultiLine || notes.Contains("address line omitted")
                    ? "medium"
                    : "high";
        }

        return new ArchiveOrgVenueInferenceResult
        {
            ProposedVenue = string.IsNullOrWhiteSpace(parsed.Venue) ? null : parsed.Venue,
            ProposedCoverage = string.IsNullOrWhiteSpace(parsed.Coverage) ? null : parsed.Coverage,
            Confidence = confidence,
            Notes = string.Join("; ", notes.Distinct(StringComparer.Ordinal)),
            DescriptionFirstLines = lines.Take(8).ToList()
        };
    }

    private static string Normalize(string value)
    {
        return Whitespace.Replace(value, " ").Trim();
    }

    private static string NormalizeIdentity(string value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        return new string(normalized.Where(char.IsLetterOrDigit).ToArray());
    }

    private sealed record ParsedVenueCoverage(string? Venue, string? Coverage, bool IsMultiLine);
}
