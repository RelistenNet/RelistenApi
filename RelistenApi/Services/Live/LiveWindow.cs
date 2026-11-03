using System;

namespace Relisten.Services.Live
{
    /// <summary>
    /// The span of plays a live summary covers: [Start, End), whole minutes long.
    /// </summary>
    public sealed record LiveWindow(DateTime Start, DateTime End, int Minutes)
    {
        /// <summary>
        /// How often a new summary is computed. The window ends on the last completed
        /// boundary of this interval, so every request in the same interval sees the
        /// same window and can share one cached response. One query costs about half a
        /// second of database time on production; a longer interval saves little and
        /// makes a "right now" page show older plays.
        /// </summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

        public static LiveWindow EndingBefore(DateTime now, int minutes)
        {
            var utcNow = now.ToUniversalTime();
            var end = new DateTime(
                utcNow.Ticks - utcNow.Ticks % RefreshInterval.Ticks,
                DateTimeKind.Utc
            );

            return new LiveWindow(end.AddMinutes(-minutes), end, minutes);
        }

        /// <summary>When the next window will end, i.e. when this one's response goes stale.</summary>
        public DateTime NextRefreshAt => End + RefreshInterval;
    }
}
