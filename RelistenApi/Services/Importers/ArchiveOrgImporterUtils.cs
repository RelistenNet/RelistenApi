using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace Relisten.Import;

public static class ArchiveOrgImporterUtils
{
    private static readonly Regex ExtractDateFromIdentifier = new(@"(\d{4}-\d{2}-\d{2})");

    // thanks to this trouble child: https://archive.org/metadata/lotus2011-16-07.lotus2011-16-07_Neumann
    public static string? FixDisplayDate(Metadata? meta)
    {
        if (meta == null || string.IsNullOrEmpty(meta.date))
        {
            return null;
        }

        var parts = meta.date.Split('-');

        if (parts.Length == 3)
        {
            var year = parts[0];
            var month = parts[1];
            var day = parts[2];

            var changed = false;

            if (month == "00")
            {
                month = "XX";
                changed = true;
            }

            if (day == "00")
            {
                day = "XX";
                changed = true;
            }

            if (changed)
            {
                return string.Join('-', year, month, day);
            }
        }

        // 1970-03-XX or 1970-XX-XX which is okay because it is handled by the rebuild
        if (meta.date.Contains('X'))
        {
            return meta.date;
        }

        if (meta.date == "2013-14-02")
        {
            // this date from The Werks always gives us issues and TryFlippingMonthAndDate doesn't work...I suspect
            // some sort cultural issue because I cannot reproduce this locally
            return "2013-02-14";
        }

        // happy case
        if (TestDate(meta.date))
        {
            return meta.date;
        }

        var d = TryFlippingMonthAndDate(meta.date);

        if (d != null)
        {
            return d;
        }

        // try to parse it out of the identifier
        var matches = ExtractDateFromIdentifier.Match(meta.identifier);

        if (matches.Success)
        {
            var tdate = matches.Groups[1].Value;

            if (TestDate(tdate))
            {
                return tdate;
            }

            var flipped = TryFlippingMonthAndDate(tdate);

            if (flipped != null)
            {
                return flipped;
            }
        }

        return null;
    }

    private static bool TestDate(string date)
    {
        return DateTime.TryParseExact(date, "yyyy-MM-dd",
            DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal, out _);
    }

    private static string? TryFlippingMonthAndDate(string date)
    {
        // not a valid date
        var parts = date.Split('-');

        // try to see if it is YYYY-DD-MM instead
        if (parts.Length > 2 && int.TryParse(parts[1], out var month))
        {
            if (month > 12)
            {
                // rearrange to YYYY-MM-DD
                var dateStr = parts[0] + "-" + parts[2] + "-" + parts[1];

                if (TestDate(dateStr))
                {
                    return dateStr;
                }
            }
        }

        return null;
    }
}
