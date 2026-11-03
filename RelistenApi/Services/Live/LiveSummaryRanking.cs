using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Relisten.Services.Live
{
    /// <summary>A show from the window with its baseline and surprise score.</summary>
    public sealed record ScoredShow(
        LiveShowRow Row,
        double ExpectedPlays,
        double Surprise,
        bool IsAnniversary
    );

    /// <summary>An artist from the window with its surprise score.</summary>
    public sealed record ScoredArtist(LiveArtistRow Row, double Surprise);

    /// <summary>
    /// Decides which shows and artists the live summary headlines. Raw play counts put
    /// the Grateful Dead and Phish at the top of every list, so the default ranking is
    /// surprise: how far above its normal level something is right now. The response
    /// carries the score components, so this formula can be tuned without changing the
    /// response shape.
    /// </summary>
    public static class LiveSummaryRanking
    {
        /// <summary>
        /// Added to both plays and expected plays in the surprise ratio, so one play
        /// against a near-zero baseline scores (1 + 3) / (0 + 3) = 1.33 instead of
        /// infinity.
        /// </summary>
        public const double SurpriseSmoothingPlays = 3;

        /// <summary>
        /// Plays a show needs in the window to appear in shows, deep_cuts or fresh, so
        /// one stray play cannot headline the page.
        /// </summary>
        public const long MinShowPlays = 2;

        /// <summary>A show with at most this many plays over 30 days is a deep cut.</summary>
        public const long DeepCutMaxPlays30d = 25;

        /// <summary>
        /// Multiplies the surprise of a show from a past year played on its anniversary
        /// (the request's month_day). The apps feature "today in history", so those shows
        /// get hundreds of times their usual plays every year on that date; without this
        /// they fill the top of the list daily. They are still counted in on_this_day.
        /// </summary>
        public const double AnniversarySurpriseMultiplier = 0.25;

        public const int DeepCutsLimit = 10;
        public const int FreshLimit = 10;

        /// <summary>
        /// Days of the daily aggregate a show's normal rate is averaged over. Shows are
        /// too sparse for an hour-of-week baseline.
        /// </summary>
        public const int ShowBaselineDays = 30;

        /// <summary>
        /// Prior weeks an artist's same-hour-of-week baseline is averaged over. Comparing
        /// against the same hour of the week removes the daily and weekly listening cycle.
        /// </summary>
        public const int ArtistBaselineWeeks = 4;

        public static double Surprise(long plays, double expectedPlays) =>
            (plays + SurpriseSmoothingPlays) / (expectedPlays + SurpriseSmoothingPlays);

        /// <summary>The show's average 30-day play rate scaled to the window length.</summary>
        public static double ShowExpectedPlays(long plays30d, int windowMinutes) =>
            plays30d / (ShowBaselineDays * 24.0 * 60.0) * windowMinutes;

        public static IReadOnlyList<ScoredShow> ScoreShows(
            IEnumerable<LiveShowRow> shows,
            LiveSummaryRequest request,
            LiveWindow window
        )
        {
            return shows
                .Select(row =>
                {
                    var expected = ShowExpectedPlays(row.plays_30d, window.Minutes);
                    var surprise = Surprise(row.plays, expected);
                    var isAnniversary = IsPastYearAnniversary(
                        row.display_date,
                        request.MonthDay,
                        window.End.Year
                    );

                    return new ScoredShow(
                        row,
                        expected,
                        isAnniversary ? surprise * AnniversarySurpriseMultiplier : surprise,
                        isAnniversary
                    );
                })
                .ToList();
        }

        public static IReadOnlyList<ScoredShow> TopShows(
            IEnumerable<ScoredShow> shows,
            LiveSummaryRequest request
        )
        {
            var eligible = shows.Where(show => show.Row.plays >= MinShowPlays);
            var sorted =
                request.Sort == LiveSummarySort.Surprise
                    ? eligible.OrderByDescending(show => show.Surprise).ThenByPlays()
                    : eligible.OrderByDescending(show => show.Row.plays).ThenByRecency();

            return CapPerArtist(sorted, request.MaxPerArtist).Take(request.ShowsLimit).ToList();
        }

        public static IReadOnlyList<ScoredShow> DeepCuts(
            IEnumerable<ScoredShow> shows,
            LiveSummaryRequest request
        )
        {
            var deepCuts = shows
                .Where(show =>
                    show.Row.plays >= MinShowPlays && show.Row.plays_30d <= DeepCutMaxPlays30d
                )
                .OrderByDescending(show => show.Row.plays)
                .ThenByRecency();

            return CapPerArtist(deepCuts, request.MaxPerArtist).Take(DeepCutsLimit).ToList();
        }

        /// <summary>
        /// Shows from concerts in the last fresh_days days. Shows whose tape was newly
        /// added would also count, but sources.created_at is reset when a source is
        /// re-ingested, so nothing records that reliably yet.
        /// </summary>
        public static IReadOnlyList<ScoredShow> Fresh(
            IEnumerable<ScoredShow> shows,
            LiveSummaryRequest request,
            LiveWindow window
        )
        {
            var today = DateOnly.FromDateTime(window.End);
            var oldest = today.AddDays(-request.FreshDays);

            var fresh = shows
                .Where(show => show.Row.plays >= MinShowPlays)
                .Where(show =>
                    TryParseFullDate(show.Row.display_date, out var date)
                    && date >= oldest
                    && date <= today
                )
                .OrderByDescending(show => show.Row.plays)
                .ThenByRecency();

            return CapPerArtist(fresh, request.MaxPerArtist).Take(FreshLimit).ToList();
        }

        public static IReadOnlyList<ScoredArtist> TopArtists(
            IEnumerable<LiveArtistRow> artists,
            LiveSummaryRequest request
        )
        {
            var scored = artists.Select(row => new ScoredArtist(
                row,
                Surprise(row.plays, row.expected_plays)
            ));
            var sorted =
                request.Sort == LiveSummarySort.Surprise
                    ? scored
                        .OrderByDescending(artist => artist.Surprise)
                        .ThenByDescending(artist => artist.Row.plays)
                    : scored.OrderByDescending(artist => artist.Row.plays);

            return sorted
                .ThenBy(artist => artist.Row.artist_id)
                .Take(request.ArtistsLimit)
                .ToList();
        }

        /// <summary>
        /// Each artist's show with the most listened time, ties going to more plays,
        /// then the most recent play. Every show in the window is considered, unranked
        /// and uncapped, because this describes the artist rather than headlining a show.
        /// </summary>
        public static IReadOnlyDictionary<int, ScoredShow> TopShowPerArtist(
            IEnumerable<ScoredShow> shows
        )
        {
            return shows
                .GroupBy(show => show.Row.artist_id)
                .ToDictionary(
                    artistShows => artistShows.Key,
                    artistShows =>
                        artistShows
                            .OrderByDescending(show => show.Row.listened_seconds)
                            .ThenByDescending(show => show.Row.plays)
                            .ThenByRecency()
                            .First()
                );
        }

        /// <summary>
        /// Shows dated on the given MM-DD in any year. Partial dates such as 1977-05-XX
        /// never match, rather than guessing the missing day. MinShowPlays does not apply:
        /// on_this_day counts every play, and lists only shows already in shows.
        /// </summary>
        public static IReadOnlyList<ScoredShow> OnThisDay(
            IEnumerable<ScoredShow> shows,
            string monthDay
        )
        {
            return shows
                .Where(show =>
                    TryParseFullDate(show.Row.display_date, out var date)
                    && IsOnMonthDay(date, monthDay)
                )
                .ToList();
        }

        /// <summary>
        /// A show from an earlier year whose full date falls on month_day. A show played
        /// on the day it happened (this year) is news, not an anniversary.
        /// </summary>
        private static bool IsPastYearAnniversary(
            string displayDate,
            string monthDay,
            int currentYear
        ) =>
            TryParseFullDate(displayDate, out var date)
            && date.Year < currentYear
            && IsOnMonthDay(date, monthDay);

        private static bool IsOnMonthDay(DateOnly date, string monthDay) =>
            date.ToString("MM-dd", CultureInfo.InvariantCulture) == monthDay;

        /// <summary>
        /// Keeps each artist's first <paramref name="maxPerArtist"/> shows in the given
        /// order, so one band cannot fill a list. Zero means no cap.
        /// </summary>
        public static IReadOnlyList<ScoredShow> CapPerArtist(
            IEnumerable<ScoredShow> sortedShows,
            int maxPerArtist
        )
        {
            if (maxPerArtist == 0)
            {
                return sortedShows.ToList();
            }

            var capped = new List<ScoredShow>();
            var keptPerArtist = new Dictionary<int, int>();
            foreach (var show in sortedShows)
            {
                keptPerArtist.TryGetValue(show.Row.artist_id, out var kept);
                if (kept < maxPerArtist)
                {
                    capped.Add(show);
                    keptPerArtist[show.Row.artist_id] = kept + 1;
                }
            }

            return capped;
        }

        private static bool TryParseFullDate(string displayDate, out DateOnly date) =>
            DateOnly.TryParseExact(
                displayDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date
            );

        private static IOrderedEnumerable<ScoredShow> ThenByPlays(
            this IOrderedEnumerable<ScoredShow> shows
        ) => shows.ThenByDescending(show => show.Row.plays).ThenByRecency();

        // Final tie-breaks: most recently played first, then show id so the order is stable.
        private static IOrderedEnumerable<ScoredShow> ThenByRecency(
            this IOrderedEnumerable<ScoredShow> shows
        ) =>
            shows
                .ThenByDescending(show => show.Row.last_played_at)
                .ThenBy(show => show.Row.show_id);
    }
}
