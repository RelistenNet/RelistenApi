using System.Globalization;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Calendar components used only for shadow correspondence. Unknown month/day components
/// remain unknown; the catalog's sorting placeholders must not become date evidence.
/// </summary>
internal sealed record ShadowPerformanceDate(int Year, int? Month, int? Day)
{
    private static readonly Regex Components = new(
        @"^(?<year>\d{4})(?:-(?<month>\d{2}|[Xx?]{2})(?:-(?<day>\d{2}|[Xx?]{2}))?)?$",
        RegexOptions.CultureInvariant
    );

    public bool IsExact => Month.HasValue && Day.HasValue;

    public bool CompatibleWith(ShadowPerformanceDate other) =>
        Year == other.Year
        && (!Month.HasValue || !other.Month.HasValue || Month == other.Month)
        && (!Day.HasValue || !other.Day.HasValue || Day == other.Day);

    public static ShadowPerformanceDate? Parse(string? literal, bool setlistFm = false)
    {
        if (string.IsNullOrWhiteSpace(literal))
        {
            return null;
        }
        var text = literal.Trim();
        var match = Components.Match(text);
        if (match.Success)
        {
            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            var month = Component(match.Groups["month"].Value);
            var day = Component(match.Groups["day"].Value);
            if (year < 1000 || month is < 1 or > 12 || day is < 1 or > 31)
            {
                return null;
            }
            if (month.HasValue && day.HasValue && day > DateTime.DaysInMonth(year, month.Value))
            {
                return null;
            }
            return new(year, month, day);
        }

        // These are actual provider representations. In particular, dd-MM-yyyy is only
        // interpreted for setlist.fm, where the provider contract defines that ordering.
        string[] formats = setlistFm
            ? ["dd-MM-yyyy"]
            :
            [
                "MMMM d yyyy",
                "MMMM d, yyyy",
                "d MMMM yyyy",
                "d-MMMM-yyyy",
                "yyyy-MM-dd'T'HH:mm:ss",
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:sszzz",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            ];
        return
            DateTimeOffset.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            )
            && date.Year >= 1000
            ? new(date.Year, date.Month, date.Day)
            : null;
    }

    private static int? Component(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number != 0
            ? number
            : null;
}
