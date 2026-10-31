using Relisten.Api.Models;

namespace Relisten.Api.Models.Api.V4;

// One score conversion for ordinary resources and rankings; selection stays in readers.
public static partial class V4CatalogProjection
{
    private static PopularityResource ProjectPopularity(PopularityMetrics metrics)
    {
        static PopularityWindowResource Window(PopularityWindowMetrics value) =>
            new(
                value.plays,
                System.Math.Round(value.hours, 4),
                System.Math.Round(value.hot_score, 4)
            );
        return new(
            System.Math.Round(metrics.momentum_score, 4),
            System.Math.Round(metrics.trend_ratio, 4),
            new(
                Window(metrics.windows.hours_48h),
                Window(metrics.windows.days_7d),
                Window(metrics.windows.days_30d)
            )
        );
    }
}
