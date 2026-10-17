using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Relisten.Data;

namespace Relisten.Ingestion.Providers.TapersSection;

[JsonConverter(typeof(JsonStringEnumConverter<TapersSectionDatePrecision>))]
public enum TapersSectionDatePrecision
{
    [JsonStringEnumMemberName("day")]
    Day,

    [JsonStringEnumMemberName("month")]
    Month,

    [JsonStringEnumMemberName("year")]
    Year,

    [JsonStringEnumMemberName("partial")]
    Partial,
}

[JsonConverter(typeof(JsonStringEnumConverter<TapersSectionDateNoteCode>))]
public enum TapersSectionDateNoteCode
{
    [JsonStringEnumMemberName("partial_date")]
    PartialDate,

    [JsonStringEnumMemberName("identifier_date_disagreement")]
    IdentifierDateDisagreement,

    [JsonStringEnumMemberName("metadata_date_disagreement")]
    MetadataDateDisagreement,
}

/// <summary>Nonblocking date context. Publication holds belong to interpretation Reasons.</summary>
public sealed record TapersSectionDateNote(
    TapersSectionDateNoteCode Code,
    TapersSectionEvidence[] Evidence
);

/// <summary>Explains the selected DisplayDate without repeating that date or treating notes as holds.</summary>
public sealed record TapersSectionDateDetails(
    TapersSectionDatePrecision Precision,
    TapersSectionEvidence SelectedEvidence,
    TapersSectionDateNote[] Notes
);

internal sealed record TapersSectionDateResolution(
    string? DisplayDate,
    TapersSectionDateDetails? Details
);

/// <summary>
/// Resolves mutable performance metadata while retaining immutable identifier dates as diagnostics.
/// Known-year partial dates preserve unknown components as XX. Descriptions can hold conflicting
/// assignments but cannot supply a date; identifier dates neither select nor refine one.
/// </summary>
internal sealed class TapersSectionDateResolver
{
    private const string IsoPattern = @"\d{4}(?:-(?:\d{2}|XX|\?\?)(?:-(?:\d{2}|XX|\?\?))?)?";
    private static readonly Regex IsoDates = new(
        @"(?<!\d)(?<date>\d{4}-(?:\d{2}|XX|\?\?)(?:-(?:\d{2}|XX|\?\?))?)(?![\d-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex BareYears = new(
        @"(?<![\w-])(?<date>\d{4})(?![\w-])",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex WrittenDates = new(
        @"\b(?<date>(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}|\d{1,2}[ -](?:January|February|March|April|May|June|July|August|September|October|November|December)[ -]\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex NonPerformanceTitle = new(
        @"\b(?:upload(?:ed)?|remaster(?:ed)?|published|release date)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    internal TapersSectionDateResolution Resolve(
        JObject metadata,
        ArchiveItemId item,
        List<TapersSectionReason> reasons,
        List<TapersSectionEvidence> evidence
    )
    {
        var structured = ReadStructuredDates(metadata, reasons, evidence);
        var titles = ReadTitleDates(metadata, evidence);
        // Bare four-digit identifier components also encode audio formats, such as .2496.
        // Only dates with explicit components can supply identifier diagnostics.
        var identifier = FindDates(
                item.Value,
                "item_identifier",
                "identifier_iso_date",
                includeStandaloneYears: false
            )
            .ToArray();
        var descriptions = ReadDescriptionDates(metadata).ToArray();
        evidence.AddRange(identifier.Select(date => date.Evidence));
        evidence.AddRange(descriptions.Select(date => date.Evidence));

        var selected = SelectMetadataDate(structured, titles);
        if (selected == null)
        {
            reasons.Add(TapersSectionReason.MissingDate);
            return new(null, null);
        }

        var notes = new List<TapersSectionDateNote>();
        var current = structured
            .Concat(titles.Select(title => title.Date))
            .Concat(descriptions)
            .ToArray();
        if (
            current.Select(date => date.Year).Distinct().Skip(1).Any()
            || current
                .Where(date => date.Month.HasValue)
                .Select(date => date.Month)
                .Distinct()
                .Skip(1)
                .Any()
            || current
                .Where(date => date.Day.HasValue)
                .Select(date => date.Day)
                .Distinct()
                .Skip(1)
                .Any()
        )
        {
            reasons.Add(TapersSectionReason.ConflictingDate);
        }
        if (current.Any(date => date.DisplayDate != selected.DisplayDate))
        {
            notes.Add(
                new(
                    TapersSectionDateNoteCode.MetadataDateDisagreement,
                    current.Select(date => date.Evidence).ToArray()
                )
            );
        }
        foreach (var year in Strings(metadata["year"]))
        {
            if (
                Regex.IsMatch(year, @"^\d{4}$", RegexOptions.CultureInvariant)
                && year != selected.Year.ToString(CultureInfo.InvariantCulture)
            )
            {
                reasons.Add(TapersSectionReason.ConflictingDate);
                evidence.Add(new("metadata.year", year, "structured_year_conflict"));
            }
        }
        if (identifier.Any(date => !Compatible(selected, date)))
        {
            notes.Add(
                new(
                    TapersSectionDateNoteCode.IdentifierDateDisagreement,
                    identifier.Select(date => date.Evidence).ToArray()
                )
            );
        }
        if (selected.Precision != TapersSectionDatePrecision.Day)
        {
            notes.Add(new(TapersSectionDateNoteCode.PartialDate, [selected.Evidence]));
        }
        return new(
            selected.DisplayDate,
            new(selected.Precision, selected.Evidence, notes.ToArray())
        );
    }

    private static ParsedDate[] ReadStructuredDates(
        JObject metadata,
        List<TapersSectionReason> reasons,
        List<TapersSectionEvidence> evidence
    )
    {
        var token = metadata["date"];
        if (
            token != null
            && token.Type != JTokenType.Null
            && token.Type != JTokenType.String
            && (token is not JArray array || array.Any(value => value.Type != JTokenType.String))
        )
        {
            reasons.Add(TapersSectionReason.AmbiguousDate);
        }
        var values = Strings(token).ToArray();
        var result = new List<ParsedDate>();
        for (var index = 0; index < values.Length; index++)
        {
            var raw = values[index];
            var match = Regex.Match(
                raw,
                "^(?<date>" + IsoPattern + @")(?:T00:00:00(?:\.000)?Z)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
            if (match.Success && TryParse(match.Groups["date"].Value, out var date))
            {
                var rule =
                    date.Precision == TapersSectionDatePrecision.Day
                        ? "complete_structured_date"
                        : "partial_structured_date";
                var retained = date with
                {
                    Evidence = new(Locator(token, "date", index), raw, rule),
                };
                result.Add(retained);
                evidence.Add(retained.Evidence);
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                reasons.Add(TapersSectionReason.AmbiguousDate);
                evidence.Add(new(Locator(token, "date", index), raw, "unusable_structured_date"));
            }
        }
        return result.ToArray();
    }

    private static TitleDate[] ReadTitleDates(
        JObject metadata,
        List<TapersSectionEvidence> evidence
    )
    {
        var titles = Strings(metadata["title"]).ToArray();
        var allowFallback = titles.All(title => !NonPerformanceTitle.IsMatch(title));
        var result = new List<TitleDate>();
        for (var index = 0; index < titles.Length; index++)
        {
            foreach (
                var date in FindDates(
                    titles[index],
                    Locator(metadata["title"], "title", index),
                    "full_year_title_date"
                )
            )
            {
                var supported =
                    allowFallback
                    && IsPerformanceTitle(titles[index], date.Evidence, metadata["creator"]);
                if (date.Evidence.Value.Length == 4 && !supported)
                {
                    // A year in a performer or venue name is not performance-date evidence.
                    continue;
                }
                evidence.Add(date.Evidence);
                result.Add(new(date, supported));
            }
        }
        return result.ToArray();
    }

    private static IEnumerable<ParsedDate> ReadDescriptionDates(JObject metadata)
    {
        var descriptions = Strings(metadata["description"]).ToArray();
        for (var index = 0; index < descriptions.Length; index++)
        {
            foreach (
                Match label in Regex.Matches(
                    descriptions[index],
                    @"(?:^|[\r\n]|<br\s*/?>|<div>|<p>)\s*(?:performance date|show date|date)\s*:\s*(?<value>[^<\r\n]+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
            {
                foreach (
                    var date in FindDates(
                        label.Groups["value"].Value,
                        Locator(metadata["description"], "description", index),
                        "explicit_description_date"
                    )
                )
                {
                    yield return date with
                    {
                        Evidence = date.Evidence with
                        {
                            Start = label.Groups["value"].Index + date.Evidence.Start,
                        },
                    };
                }
            }
        }
    }

    private static ParsedDate? SelectMetadataDate(ParsedDate[] structured, TitleDate[] titles)
    {
        var selected = structured.OrderByDescending(date => date.KnownParts).FirstOrDefault();
        var supported = titles
            .Where(title => title.Supported)
            .Select(title => title.Date)
            .ToArray();
        if (selected == null)
        {
            return supported.OrderByDescending(date => date.KnownParts).FirstOrDefault();
        }

        // Explicit partial performance titles can explain first-of-month/year placeholders.
        // They cannot override a different known month/day; that remains a metadata conflict.
        var placeholder = supported.FirstOrDefault(date => IsPlaceholderFor(selected, date));
        if (placeholder != null)
        {
            return placeholder;
        }
        return supported
                .Where(date => Compatible(selected, date) && date.KnownParts > selected.KnownParts)
                .OrderByDescending(date => date.KnownParts)
                .FirstOrDefault()
            ?? selected;
    }

    private static bool IsPlaceholderFor(ParsedDate structured, ParsedDate title) =>
        structured.Precision == TapersSectionDatePrecision.Day
        && title.Precision != TapersSectionDatePrecision.Day
        && Compatible(structured, title)
        && (title.Month.HasValue || structured.Month == 1)
        && (title.Day.HasValue || structured.Day == 1);

    private static bool Compatible(ParsedDate left, ParsedDate right) =>
        left.Year == right.Year
        && (!left.Month.HasValue || !right.Month.HasValue || left.Month == right.Month)
        && (!left.Day.HasValue || !right.Day.HasValue || left.Day == right.Day);

    private static bool IsPerformanceTitle(
        string title,
        TapersSectionEvidence date,
        JToken? creator
    )
    {
        if (NonPerformanceTitle.IsMatch(title))
        {
            return false;
        }
        if (
            date.Value.Length != 4
            && Regex.IsMatch(
                title,
                @"\blive at\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return true;
        }
        var offset = date.Start!.Value;
        var prefix = title[..offset].Trim().TrimEnd('-', ':', '—').Trim();
        var identity =
            prefix.Length > 0
                ? prefix
                : title[(offset + date.Length!.Value)..].Trim().TrimStart('-', ':', '—').Trim();
        var key = ArtistRegistryService.NormalizeIdentityKey(identity);
        return Strings(creator)
            .Any(label => ArtistRegistryService.NormalizeIdentityKey(label) == key);
    }

    private static IEnumerable<ParsedDate> FindDates(
        string text,
        string locator,
        string rule,
        bool includeStandaloneYears = true
    )
    {
        var written = WrittenDates.Matches(text).Cast<Match>().ToArray();
        var numeric = IsoDates.Matches(text).Cast<Match>();
        if (includeStandaloneYears)
        {
            numeric = numeric.Concat(BareYears.Matches(text).Cast<Match>());
        }

        var matches = written
            .Concat(
                numeric.Where(match =>
                    !written.Any(full =>
                        match.Index >= full.Index && match.Index < full.Index + full.Length
                    )
                )
            )
            .OrderBy(match => match.Index);
        foreach (var match in matches)
        {
            if (TryParse(match.Groups["date"].Value, out var date))
            {
                yield return date with
                {
                    Evidence = new(locator, match.Value, rule, match.Index, match.Length),
                };
            }
        }
    }

    private static bool TryParse(string value, out ParsedDate date)
    {
        date = null!;
        if (
            Regex.IsMatch(
                value,
                "^" + IsoPattern + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            var parts = value.Split('-');
            var year = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var month = parts.Length > 1 ? Component(parts[1]) : null;
            var day = parts.Length > 2 ? Component(parts[2]) : null;
            if (
                year < 1000
                || month is < 1 or > 12
                || day is < 1 or > 31
                || month.HasValue && day.HasValue && day > DateTime.DaysInMonth(year, month.Value)
            )
            {
                return false;
            }
            date = new(year, month, day, null!);
            return true;
        }
        if (
            DateOnly.TryParseExact(
                value,
                ["MMMM d yyyy", "MMMM d, yyyy", "d MMMM yyyy", "d-MMMM-yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed
            )
            && parsed.Year >= 1000
        )
        {
            date = new(parsed.Year, parsed.Month, parsed.Day, null!);
            return true;
        }
        return false;
    }

    private static int? Component(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number != 0
            ? number
            : null;

    private static IEnumerable<string> Strings(JToken? token) =>
        token switch
        {
            { Type: JTokenType.String } => [token.Value<string>()!],
            JArray array => array
                .Where(value => value.Type == JTokenType.String)
                .Select(value => value.Value<string>()!),
            _ => [],
        };

    private static string Locator(JToken? token, string field, int index) =>
        token is JArray ? $"metadata.{field}[{index}]" : $"metadata.{field}";

    private sealed record TitleDate(ParsedDate Date, bool Supported);

    private sealed record ParsedDate(int Year, int? Month, int? Day, TapersSectionEvidence Evidence)
    {
        internal int KnownParts => 1 + (Month.HasValue ? 1 : 0) + (Day.HasValue ? 1 : 0);
        internal string DisplayDate =>
            $"{Year:D4}-{(Month.HasValue ? Month.Value.ToString("D2", CultureInfo.InvariantCulture) : "XX")}-{(Day.HasValue ? Day.Value.ToString("D2", CultureInfo.InvariantCulture) : "XX")}";
        internal TapersSectionDatePrecision Precision =>
            Month.HasValue
                ? Day.HasValue
                    ? TapersSectionDatePrecision.Day
                    : TapersSectionDatePrecision.Month
                : Day.HasValue
                    ? TapersSectionDatePrecision.Partial
                    : TapersSectionDatePrecision.Year;
    }
}
