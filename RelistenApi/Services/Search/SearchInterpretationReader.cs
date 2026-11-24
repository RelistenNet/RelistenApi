using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Relisten.Search;

internal static partial class SearchInterpretationReader
{
    private static readonly Dictionary<string, int> Months = ReadMonths();
    private static readonly string MonthNames = string.Join('|', Months.Keys);

    private static readonly Regex[] FullDates =
    [
        IsoDate(),
        NumericDate(),
        new(
            $@"\b(?<month>{MonthNames})\s+(?<day>\d{{1,2}})(?:st|nd|rd|th)?,?\s+(?<year>\d{{4}}|\d{{2}})\b",
            RegexOptions.Compiled
        ),
        new(
            $@"\b(?<day>\d{{1,2}})\s+(?<month>{MonthNames})\s+(?<year>\d{{4}}|\d{{2}})\b",
            RegexOptions.Compiled
        ),
    ];

    public static SearchQuery Read(
        SearchInput input,
        CatalogQueryEvidence evidence,
        int? currentYear = null,
        bool literal = false
    )
    {
        var yearLimit = currentYear ?? DateTime.UtcNow.Year;
        var vocabulary = evidence.Vocabulary;
        var selected = input.ArtistId is { } id
            ? vocabulary.ArtistSlugsById.GetValueOrDefault(id)
            : null;
        var nickname =
            literal || input.Ignore?.Contains("artist") == true
                ? null
                : vocabulary.ReadReviewedArtist(SearchText.Fold(input.Text), selected);
        var expanded = ExpandInteriorYear(input.Text, vocabulary, yearLimit);
        var query = ReadDateIntent(
            expanded,
            selected,
            input.Country,
            yearLimit,
            vocabulary,
            literal,
            nickname
        );
        if (literal)
        {
            var original = query.Date != null ? query.Terms : SearchText.Fold(input.Text);
            return IgnoreChips(
                query with
                {
                    Terms = original,
                    Country = query.Date != null ? query.Country : input.Country,
                    Year = null,
                    Month = null,
                    DayKey = null,
                    Reading = "literal",
                },
                input
            );
        }

        if (
            query.DayKey != null
            && query.ImpliedArtist == null
            && vocabulary.ArtistNames.TryGetValue(query.Terms, out var dayArtist)
            && (selected == null || selected == dayArtist)
        )
        {
            query = query with { Terms = "", ImpliedArtist = dayArtist };
        }

        query = ReadNamedTour(
            query,
            expanded,
            selected,
            input.Country,
            vocabulary,
            yearLimit,
            nickname
        );
        query = ReadCuratedRoute(query);
        query = query with { Chronological = WantsChronology(query, expanded, vocabulary) };
        query = ChooseReading(input.Text, query, evidence, nickname);

        if (
            query.Artist is { } artist
            && !query.HasDate
            && query.Country == null
            && !query.Soundboard
            && !query.Matrix
            && vocabulary.CompactTitle(artist, query.Terms) is { } title
        )
        {
            query = query with { Terms = title };
        }

        return IgnoreChips(query, input);
    }

    private static SearchQuery ReadDateIntent(
        string text,
        string? selected,
        string? country,
        int currentYear,
        SearchVocabulary vocabulary,
        bool literal,
        SearchReviewedNicknames.ArtistNickname? nickname
    )
    {
        var query = ReadSyntax(text, selected, country, currentYear, vocabulary, literal, nickname);
        if (query.Date != null)
        {
            return query;
        }

        var monthDay = MonthDay().Match(text);
        if (
            monthDay.Success
            && Number(monthDay, "month") is int month
            && month is >= 1 and <= 12
            && Number(monthDay, "day") is int day
            && day >= 1
            && day <= DateTime.DaysInMonth(2000, month)
        )
        {
            return ReadSyntax(
                Remove(text, monthDay),
                selected,
                country,
                currentYear,
                vocabulary,
                literal,
                nickname
            ) with
            {
                DayKey = 100 * month + day,
            };
        }

        return query;
    }

    private static SearchQuery ReadSyntax(
        string text,
        string? selected,
        string? country,
        int currentYear,
        SearchVocabulary vocabulary,
        bool literal = false,
        SearchReviewedNicknames.ArtistNickname? nickname = null
    )
    {
        var remaining = text.ToLowerInvariant();
        var (withoutDate, date) = ExtractDate(remaining);
        remaining = SearchText.Fold(withoutDate);

        var (withoutArtist, implied) = literal
            ? (remaining, null)
            : ExtractArtist(remaining, selected, vocabulary, nickname, date != null);
        var (withoutYear, year, month) = ExtractYear(
            withoutArtist,
            date,
            selected ?? implied,
            currentYear
        );
        var (terms, dayKey, region) = ExtractCalendarAndCountry(withoutYear, country);
        var soundboard = Soundboard().IsMatch(terms);
        var matrix = HasPhrase(terms, "matrix");
        var (withoutOrder, order) = ExtractOrder(terms);
        terms = SearchText.Fold(RecordingTerms().Replace(withoutOrder, " "));

        return new SearchQuery(
            terms,
            selected,
            implied,
            region,
            date,
            year,
            month,
            dayKey,
            soundboard,
            matrix,
            order
        );
    }

    private static (string Text, int? Date) ExtractDate(string text)
    {
        foreach (var pattern in FullDates)
        {
            var match = pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var month =
                Number(match, "month") ?? Months.GetValueOrDefault(match.Groups["month"].Value);
            var day = Number(match, "day") ?? 0;
            var year = FullYear(Number(match, "year") ?? 0);
            if (
                year > 0
                && month is >= 1 and <= 12
                && day >= 1
                && day <= DateTime.DaysInMonth(year, month)
            )
            {
                return (Remove(text, match), year * 10_000 + month * 100 + day);
            }

            break;
        }

        var newYearsEve = NewYearsEveWithYear().Match(text);
        if (newYearsEve.Success && Number(newYearsEve, "year") is int eveYear)
        {
            return (Remove(text, newYearsEve), FullYear(eveYear) * 10_000 + 1231);
        }

        return (text, null);
    }

    private static (string Text, string? Artist) ExtractArtist(
        string text,
        string? selected,
        SearchVocabulary vocabulary,
        SearchReviewedNicknames.ArtistNickname? nickname,
        bool hasFullDate
    )
    {
        for (var end = text.LastIndexOf(' '); end > 0; end = text.LastIndexOf(' ', end - 1))
        {
            var name = text[..end];
            if (
                !vocabulary.ArtistNames.TryGetValue(name, out var artist)
                || (selected != null && selected != artist)
                || vocabulary.IsKnownPlace(name)
                || vocabulary.ExactPlaceSong(text, selected) != null
            )
            {
                continue;
            }

            var remainder = text[(end + 1)..];
            // The early syntax pass needs the artist for short dates and a
            // full name can disambiguate a shorter nickname such as "dead".
            // Other readings need catalog evidence before consuming words.
            if (
                hasFullDate
                || FourDigitYear().IsMatch(remainder)
                || ShortYearAtEnd().IsMatch(remainder)
                || nickname != null && HasPhrase(name, nickname.Nickname)
                || vocabulary.ExactPlaceSong(remainder, artist) != null
            )
            {
                return (remainder, artist);
            }
        }

        // Some artists publish an initialism as their catalog slug. A verified
        // leading slug supplies the same context as a full name during parsing.
        var space = text.IndexOf(' ');
        if (
            nickname == null
            && space > 0
            && vocabulary.ReadArtistInitialism(text, selected) is { } initialism
            && text[..space] == initialism.Artist.ArtistSlug
            && !vocabulary.IsKnownPlace(initialism.Artist.ArtistSlug)
        )
        {
            return (initialism.Remainder, initialism.Artist.ArtistSlug);
        }

        return nickname != null && HasPhrase(text, nickname.Nickname)
            ? (RemovePhrase(text, nickname.Nickname), nickname.ArtistSlug)
            : (text, null);
    }

    private static (string Text, int? Year, int? Month) ExtractYear(
        string text,
        int? date,
        string? artist,
        int currentYear
    )
    {
        if (date != null)
        {
            return (text, null, null);
        }

        var match = FourDigitYear().Match(text);
        if (!match.Success)
        {
            match = ShortYearAtEnd().Match(text);
            if (
                !match.Success
                || Number(match, "year") is not int shortYear
                || !(shortYear >= 65 || artist != null && FullYear(shortYear) <= currentYear)
            )
            {
                return (text, null, null);
            }
        }

        text = Remove(text, match);
        var year = FullYear(Number(match, "year")!.Value);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Months.TryGetValue(word, out var month))
            {
                return (RemovePhrase(text, word), year, month);
            }
        }

        return (text, year, null);
    }

    private static (string Text, int? DayKey, string? Country) ExtractCalendarAndCountry(
        string terms,
        string? country
    )
    {
        int? dayKey = null;
        if (HasPhrase(terms, "nye"))
        {
            dayKey = 1231;
            terms = RemovePhrase(terms, "nye");
        }

        if (HasPhrase(terms, "halloween"))
        {
            dayKey = 1031;
            terms = RemovePhrase(terms, "halloween");
        }

        if (HasPhrase(terms, "japan"))
        {
            country = "JP";
            terms = RemovePhrase(terms, "japan");
        }

        return (terms, dayKey, country);
    }

    private static (string Text, string Order) ExtractOrder(string text)
    {
        var clean = SearchText.Fold(text);
        foreach (
            var (marker, order) in new[]
            {
                ("longest", "longest"),
                ("last", "last"),
                ("best", "popular"),
            }
        )
        {
            if (clean.StartsWith(marker + " ", StringComparison.Ordinal))
            {
                return (clean[(marker.Length + 1)..], order);
            }
        }
        return (clean, "regular");
    }

    private static SearchQuery ReadNamedTour(
        SearchQuery query,
        string text,
        string? selected,
        string? country,
        SearchVocabulary vocabulary,
        int currentYear,
        SearchReviewedNicknames.ArtistNickname? nickname
    )
    {
        var match = SeasonTour().Match(text);
        if (!match.Success || query.Date != null)
        {
            return query;
        }

        var year = FullYear(Number(match, "year")!.Value);
        var season = SearchText.Fold(match.Groups["season"].Value);
        var tour = new[] { $"{season} tour {year}", $"{season} {year}" }.FirstOrDefault(
            vocabulary.TourNames.Contains
        );
        if (tour == null || query.Year is int existing && existing != year)
        {
            return query;
        }

        var remaining = ReadSyntax(
            Remove(text, match),
            selected,
            country,
            currentYear,
            vocabulary,
            nickname: nickname
        );
        return remaining.Year is int other && other != year
            ? query
            : remaining with
            {
                Year = year,
                Tour = tour,
            };
    }

    private static SearchQuery ReadCuratedRoute(SearchQuery query)
    {
        if (query.Terms.Length == 0 || query.Country != null || query.Matrix)
        {
            return query;
        }

        var matched = SearchReviewedNamedShows
            .Routes.Where(route => query.Artist == null || route.Artist == query.Artist)
            .Where(route =>
                route.Song != null
                    ? query.Terms == route.Terms
                    : (
                        query.Terms == route.Terms
                        || query.Terms.StartsWith(route.Terms + " ", StringComparison.Ordinal)
                    )
                        && (
                            query.Artist != null
                            || !route.RequiresArtist && query.Terms != route.Terms
                        )
            )
            .ToArray();

        if (matched.Length != 1 || !query.MatchesDate(matched[0].Date))
        {
            return query;
        }

        var route = matched[0];
        return query with
        {
            Terms = route.Song ?? query.Terms[route.Terms.Length..].Trim(),
            ImpliedArtist = route.Artist,
            Date = route.Date,
            CuratedRoute = route.Song == null ? "show" : "performance",
        };
    }

    private static bool WantsChronology(
        SearchQuery query,
        string text,
        SearchVocabulary vocabulary
    ) =>
        query.Order == "regular"
        && (
            (query.DayKey != null && (Holidays().IsMatch(text) || query.Terms.Length == 0))
            || (
                query.Year != null
                && query.Date == null
                && query.Artist is { } artist
                && vocabulary.FindSong(artist, query.Terms) != null
            )
        );

    private static SearchQuery ChooseReading(
        string text,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        SearchReviewedNicknames.ArtistNickname? nickname
    )
    {
        var vocabulary = evidence.Vocabulary;
        if (
            !query.HasDate
            && query.Country == null
            && !query.Soundboard
            && !query.Matrix
            && query.Order == "regular"
        )
        {
            var original = SearchText.Fold(text);
            if (nickname is { MatchAsName: false } && original == nickname.Nickname)
            {
                return query with
                {
                    Terms = vocabulary.ArtistDocuments[nickname.ArtistSlug].Name,
                    ImpliedArtist = null,
                    Reading = "artist_nickname",
                    ReadingArtist = nickname.ArtistSlug,
                };
            }

            var (artist, kind) = vocabulary.FullArtist(original, query.SelectedArtist);
            var verifiedPrefix =
                kind != "artist_identity"
                    ? vocabulary.VerifiedArtistNamePrefix(original, query.SelectedArtist)
                    : null;
            if (verifiedPrefix != null)
            {
                artist = verifiedPrefix;
                kind = "artist_name_prefix";
            }

            if (artist != null && vocabulary.ArtistDocuments.TryGetValue(artist, out var document))
            {
                var exact = vocabulary
                    .Lookup(original, query.SelectedArtist, limit: 12)
                    .Any(result => result.Id == document.Id && result.CatalogQuality >= 5);
                return query with
                {
                    Terms = exact || kind == "artist_name_prefix" ? original : document.Name,
                    ImpliedArtist = null,
                    Reading = kind,
                    ReadingArtist = artist,
                    VerifiedArtistPrefix = verifiedPrefix != null,
                };
            }
        }

        if (query.Terms.Length == 0 || query.Artist != null)
        {
            return query;
        }

        var supported = vocabulary
            .SupportedArtistRemainders(query.Terms, query.SelectedArtist)
            .ToArray();
        if (supported.Length == 0)
        {
            return ReadPlayedArtistTitle(query, evidence);
        }

        if (vocabulary.Places(query.Terms).Any() && evidence.Passages.Find(query).Count > 0)
        {
            return query with { Reading = "verified_passage_with_place" };
        }

        var highest = supported.Max(candidate => candidate.Quality);
        var strongest = supported
            .Where(candidate => candidate.Quality == highest)
            .Distinct()
            .ToArray();
        return strongest.Length == 1
            ? query with
            {
                ImpliedArtist = strongest[0].Artist,
                Terms = strongest[0].Remainder,
                Reading = "artist_with_context",
            }
            : query;
    }

    private static SearchQuery ReadPlayedArtistTitle(
        SearchQuery query,
        CatalogQueryEvidence evidence
    )
    {
        var vocabulary = evidence.Vocabulary;
        var terms = query.Terms;
        for (var end = terms.LastIndexOf(' '); end > 0; end = terms.LastIndexOf(' ', end - 1))
        {
            var name = terms[..end];
            if (
                !vocabulary.ArtistNames.TryGetValue(name, out var artist)
                || vocabulary.IsKnownPlace(name)
            )
            {
                continue;
            }

            // Playback can verify an imported title absent from the song vocabulary.
            // Keep the compound literal when the remainder also names a place and song.
            var title = terms[(end + 1)..];
            if (
                evidence.TitlePlays(artist, title) > 0
                && vocabulary.ExactPlaceSong(title, null) == null
            )
            {
                return query with { ImpliedArtist = artist, Terms = title };
            }
        }

        return query;
    }

    private static string ExpandInteriorYear(
        string text,
        SearchVocabulary vocabulary,
        int currentYear
    )
    {
        if (NumericDatePart().IsMatch(text) || FourDigitYear().IsMatch(text))
        {
            return text;
        }

        var shortYears = InteriorYear().Matches(text);
        if (shortYears.Count == 0)
        {
            return text;
        }

        var folded = SearchText.Fold(text);
        if (
            vocabulary.ExactSongIds(folded, null).Length > 0
            || vocabulary.ArtistNames.ContainsKey(folded)
        )
        {
            return text;
        }

        foreach (Match match in shortYears)
        {
            // A two-digit number in a title is only a year if the catalog could
            // already contain that year and the surrounding words support a place.
            var year = FullYear(Number(match, "year")!.Value);
            if (year > currentYear)
            {
                continue;
            }

            var left = SearchText.Fold(text[..match.Index]);
            var right = SearchText.Fold(text[(match.Index + match.Length)..]);
            if (left.Length == 0 || !vocabulary.Places(left).Any())
            {
                continue;
            }

            var potentialTitle = SearchText.Fold(match.Value + " " + right);
            if (
                vocabulary
                    .Lookup(potentialTitle)
                    .Any(result => result.Kind == "song" && result.Name == potentialTitle)
            )
            {
                continue;
            }

            return text[..match.Index]
                + year.ToString(CultureInfo.InvariantCulture)
                + text[(match.Index + match.Length)..];
        }

        return text;
    }

    private static SearchQuery IgnoreChips(SearchQuery query, SearchInput input)
    {
        if (input.Ignore is not { Count: > 0 } ignore)
        {
            return query;
        }

        return query with
        {
            ImpliedArtist = ignore.Contains("artist") ? null : query.ImpliedArtist,
            ReadingArtist = ignore.Contains("artist") ? null : query.ReadingArtist,
            Country = ignore.Contains("country") ? input.Country : query.Country,
            Date = ignore.Contains("date") ? null : query.Date,
            Year = ignore.Contains("year") ? null : query.Year,
            Month = ignore.Contains("month") ? null : query.Month,
            DayKey = ignore.Contains("day") ? null : query.DayKey,
            Soundboard = !ignore.Contains("soundboard") && query.Soundboard,
            Matrix = !ignore.Contains("matrix") && query.Matrix,
            Tour = ignore.Contains("run") ? null : query.Tour,
            Order = ignore.Contains("order") ? "regular" : query.Order,
        };
    }

    internal static bool HasPhrase(string text, string phrase) =>
        (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);

    private static string RemovePhrase(string text, string phrase) =>
        SearchText.Fold(Regex.Replace(text, @"\b" + Regex.Escape(phrase) + @"\b", " "));

    private static string Remove(string text, Match match) =>
        text[..match.Index] + " " + text[(match.Index + match.Length)..];

    private static int? Number(Match match, string name) =>
        int.TryParse(match.Groups[name].Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int FullYear(int year) => year >= 100 ? year : (year >= 65 ? 1900 : 2000) + year;

    private static Dictionary<string, int> ReadMonths()
    {
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        var months = new Dictionary<string, int>();
        for (var month = 1; month <= 12; month++)
        {
            months[SearchText.Fold(names.GetMonthName(month))] = month;
            months[SearchText.Fold(names.GetAbbreviatedMonthName(month))] = month;
        }
        return months;
    }

    [GeneratedRegex(@"\b(?<year>19\d\d|20\d\d)[/-](?<month>\d{1,2})[/-](?<day>\d{1,2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(?<month>\d{1,2})[/-](?<day>\d{1,2})[/-](?<year>\d{4}|\d{2})\b")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<![\d/-])(?<month>\d{1,2})/(?<day>\d{1,2})(?![\d/-])")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"\b(?<year>19\d{2}|20\d{2})\b")]
    private static partial Regex FourDigitYear();

    [GeneratedRegex(@"\b(?<year>\d\d)$")]
    private static partial Regex ShortYearAtEnd();

    [GeneratedRegex(@"[/\d]\s*[-/]\s*\d")]
    private static partial Regex NumericDatePart();

    [GeneratedRegex(@"(?<![\d/-])\b(?<year>[0-9]{2})\b(?![\d/-])")]
    private static partial Regex InteriorYear();

    [GeneratedRegex(@"\bnye\s+['’]?(?<year>19\d{2}|20\d{2}|\d{2})\b")]
    private static partial Regex NewYearsEveWithYear();

    [GeneratedRegex(
        @"\b(?<season>winter|spring|summer|fall|autumn)(?:\s+tour)?\s+(?<year>19\d{2}|20\d{2}|\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SeasonTour();

    [GeneratedRegex(
        @"\b(nye|halloween)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex Holidays();

    [GeneratedRegex(@"\b(sbd|soundboard)\b")]
    private static partial Regex Soundboard();

    [GeneratedRegex(@"\b(sbd|soundboard|matrix)\b")]
    private static partial Regex RecordingTerms();
}
