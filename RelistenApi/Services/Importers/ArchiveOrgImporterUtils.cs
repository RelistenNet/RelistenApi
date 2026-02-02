using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Relisten.Vendor.ArchiveOrg.Metadata;
using Serilog;

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

            // Remap invalid months: "00" or month > 12 → "XX"
            if (month == "00" || (int.TryParse(month, out var m) && m > 12))
            {
                Log.Warning("[WEIRD_DATE] {Identifier}: Invalid month '{Month}' in date '{Date}', remapping to XX",
                    meta.identifier, month, meta.date);
                month = "XX";
                changed = true;
            }

            // Remap invalid days: "00" or day > 31 → "XX"
            if (day == "00" || (int.TryParse(day, out var dayNum) && dayNum > 31))
            {
                Log.Warning("[WEIRD_DATE] {Identifier}: Invalid day '{Day}' in date '{Date}', remapping to XX",
                    meta.identifier, day, meta.date);
                day = "XX";
                changed = true;
            }

            if (changed)
            {
                var result = string.Join('-', year, month, day);
                Log.Warning("[WEIRD_DATE] {Identifier}: Remapped '{Original}' → '{Result}'",
                    meta.identifier, meta.date, result);
                return result;
            }
        }

        // 1970-03-XX or 1970-XX-XX which is okay because it is handled by the rebuild
        if (meta.date.Contains('X'))
        {
            return meta.date;
        }

        // happy case
        if (TestDate(meta.date))
        {
            return meta.date;
        }

        var d = TryFlippingMonthAndDate(meta.date);

        if (d != null)
        {
            Log.Warning("[WEIRD_DATE] {Identifier}: Flipped month/day '{Original}' → '{Result}'",
                meta.identifier, meta.date, d);
            return d;
        }

        // try to parse it out of the identifier
        var matches = ExtractDateFromIdentifier.Match(meta.identifier);

        if (matches.Success)
        {
            var tdate = matches.Groups[1].Value;

            if (TestDate(tdate))
            {
                Log.Warning("[WEIRD_DATE] {Identifier}: Extracted date from identifier, metadata date '{MetadataDate}' was invalid, using '{Result}'",
                    meta.identifier, meta.date, tdate);
                return tdate;
            }

            var flipped = TryFlippingMonthAndDate(tdate);

            if (flipped != null)
            {
                Log.Warning("[WEIRD_DATE] {Identifier}: Extracted and flipped date from identifier, metadata date '{MetadataDate}' was invalid, using '{Result}'",
                    meta.identifier, meta.date, flipped);
                return flipped;
            }
        }

        Log.Error("[WEIRD_DATE] {Identifier}: Unrecoverable date '{Date}' - all parsing strategies failed",
            meta.identifier, meta.date);
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
