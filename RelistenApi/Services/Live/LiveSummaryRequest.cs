using System;
using System.Globalization;

namespace Relisten.Services.Live
{
    public enum LiveSummarySort
    {
        Surprise,
        Plays,
    }

    /// <summary>
    /// The validated query for GET /api/v3/live/summary. Numeric options are clamped
    /// to their documented ranges; malformed month_day and sort values are rejected.
    /// </summary>
    public sealed record LiveSummaryRequest(
        int Minutes,
        int ShowsLimit,
        int ArtistsLimit,
        string MonthDay,
        LiveSummarySort Sort,
        int MaxPerArtist,
        int FreshDays
    )
    {
        public const int DefaultMinutes = 15;
        public const int DefaultShowsLimit = 20;
        public const int DefaultArtistsLimit = 10;
        public const int DefaultMaxPerArtist = 2;
        public const int DefaultFreshDays = 7;

        public static bool TryCreate(
            int minutes,
            int showsLimit,
            int artistsLimit,
            string? monthDay,
            string? sort,
            int maxPerArtist,
            int freshDays,
            DateTime now,
            out LiveSummaryRequest request,
            out string error
        )
        {
            request = null!;
            error = string.Empty;

            var resolvedMonthDay = monthDay ?? now.ToUniversalTime().ToString("MM-dd");
            if (!IsMonthDay(resolvedMonthDay))
            {
                error = "month_day must be MM-DD, for example 11-03.";
                return false;
            }

            if (!TryParseSort(sort, out var resolvedSort))
            {
                error = "sort must be surprise or plays.";
                return false;
            }

            request = new LiveSummaryRequest(
                Math.Clamp(minutes, 5, 60),
                Math.Clamp(showsLimit, 1, 50),
                Math.Clamp(artistsLimit, 1, 100),
                resolvedMonthDay,
                resolvedSort,
                Math.Clamp(maxPerArtist, 0, 10),
                Math.Clamp(freshDays, 1, 30)
            );
            return true;
        }

        /// <summary>
        /// Every option that changes the response, plus the window end, so each refresh
        /// interval gets its own entry. Bump the version when the response shape or the
        /// ranking constants change, so cached responses from the old code are not served.
        /// </summary>
        public string CacheKey(LiveWindow window) =>
            string.Join(
                ':',
                "live:summary:v1",
                window.End.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture),
                Minutes,
                ShowsLimit,
                ArtistsLimit,
                MonthDay,
                Sort.ToString().ToLowerInvariant(),
                MaxPerArtist,
                FreshDays
            );

        private static bool IsMonthDay(string value)
        {
            // Parsed against a leap year so 02-29 is accepted.
            return value.Length == 5
                && DateTime.TryParseExact(
                    "2000-" + value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _
                );
        }

        private static bool TryParseSort(string? value, out LiveSummarySort sort)
        {
            switch (value)
            {
                case null:
                case "surprise":
                    sort = LiveSummarySort.Surprise;
                    return true;
                case "plays":
                    sort = LiveSummarySort.Plays;
                    return true;
                default:
                    sort = LiveSummarySort.Surprise;
                    return false;
            }
        }
    }
}
