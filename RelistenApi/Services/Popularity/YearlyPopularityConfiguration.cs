using Microsoft.Extensions.Configuration;

namespace Relisten.Services.Popularity;

internal static class YearlyPopularityConfiguration
{
    internal static bool GetYearlyPopularityEnabled(this IConfiguration? configuration) =>
        configuration?.GetValue<bool>("Popularity:EnableYearlyWindow") ?? false;
}
