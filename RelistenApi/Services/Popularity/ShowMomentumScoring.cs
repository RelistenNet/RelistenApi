using System;

namespace Relisten.Services.Popularity
{
    internal static class ShowMomentumScoring
    {
        internal const int HistoricalPenaltyWindowDays = 7;
        internal const double HistoricalPenaltyHalfLifeDays = 2.0;
        private const double HistoricalPenaltyMaxReduction = 0.5;

        internal static DateTime AnniversaryDateForPlayDay(DateTime showDate, DateTime playDay)
        {
            var day = Math.Min(showDate.Day, DateTime.DaysInMonth(playDay.Year, showDate.Month));
            return new DateTime(playDay.Year, showDate.Month, day);
        }

        internal static int AnniversaryDayOffset(DateTime showDate, DateTime playDay)
        {
            var anniversary = AnniversaryDateForPlayDay(showDate, playDay);
            return (playDay.Date - anniversary.Date).Days;
        }

        internal static bool ShouldApplyHistoricalAnniversaryPenalty(DateTime showDate, DateTime playDay)
        {
            if (playDay.Year <= showDate.Year)
            {
                return false;
            }

            var dayOffset = AnniversaryDayOffset(showDate, playDay);
            return dayOffset >= 0 && dayOffset <= HistoricalPenaltyWindowDays;
        }

        internal static double OtdPenaltyWeightForDayOffset(int dayOffset)
        {
            if (dayOffset < 0 || dayOffset > HistoricalPenaltyWindowDays)
            {
                return 0;
            }

            return Math.Exp(-Math.Log(2) * (dayOffset / HistoricalPenaltyHalfLifeDays));
        }

        internal static double ComputeOtdPenaltyRatio(double weightedPenalizedPlays7d, long plays7d)
        {
            if (plays7d <= 0)
            {
                return 0;
            }

            return Math.Clamp(weightedPenalizedPlays7d / plays7d, 0, 1);
        }

        internal static double ComputeOrganicMomentumScore(double rawMomentumScore, double otdPenaltyRatio7d)
        {
            return Math.Clamp(rawMomentumScore * (1 - HistoricalPenaltyMaxReduction * otdPenaltyRatio7d), 0, 1);
        }
    }
}
