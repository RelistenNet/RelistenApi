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
        if (meta == null) return null;
        return FixDisplayDate(meta.date, meta.identifier);
    }

    public static string? FixDisplayDate(string? date, string? identifier = null)
    {
        if (string.IsNullOrEmpty(date))
        {
            return null;
        }

        // Try parsing as a valid DateTime first (handles ISO 8601 like "2011-03-30T00:00:00Z")
        // If successful, it's a valid date - just format it as yyyy-MM-dd
        // Use RoundtripKind to preserve the original date without timezone conversion
        if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd");
        }

        // DateTime.TryParse failed - date has invalid components (00, XX, >12 month, etc.)
        // Strip time component if present before proceeding with custom fixing logic
        var tIndex = date.IndexOf('T');
        if (tIndex > 0)
        {
            date = date.Substring(0, tIndex);
        }

        var parts = date.Split('-');

        if (parts.Length == 3)
        {
            var year = parts[0];
            var month = parts[1];
            var day = parts[2];

            // Handle "00" values first
            if (month == "00")
            {
                // Log.Warning("[REMAP_DATE] {Identifier}: Zero month in '{Date}', converting to XX", identifier, date);
                month = "XX";
            }
            if (day == "00")
            {
                // Log.Warning("[REMAP_DATE] {Identifier}: Zero day in '{Date}', converting to XX", identifier, date);
                day = "XX";
            }

            // Try flipping if month looks like a day (> 12 but ≤ 31)
            if (int.TryParse(month, out var monthNum) && monthNum > 12 && monthNum <= 31 &&
                int.TryParse(day, out var dayNum) && dayNum <= 12)
            {
                var flipped = $"{year}-{day}-{month}";
                if (TestDate(flipped))
                {
                    Log.Warning("[FLIP_DATE] {Identifier}: Flipped '{Original}' → '{Result}'",
                        identifier, date, flipped);
                    return flipped;
                }
            }

            // If month still invalid (> 12), convert to XX
            if (int.TryParse(month, out var monthVal) && monthVal > 12)
            {
                Log.Warning("[INVALID_DATE] {Identifier}: Invalid month '{Month}' in '{Date}', converting to XX",
                    identifier, month, date);
                month = "XX";
            }

            // If day invalid (> 31), convert to XX
            if (int.TryParse(day, out var dayVal) && dayVal > 31)
            {
                Log.Warning("[INVALID_DATE] {Identifier}: Invalid day '{Day}' in '{Date}', converting to XX",
                    identifier, day, date);
                day = "XX";
            }

            // Return if any changes were made
            var result = $"{year}-{month}-{day}";
            if (result != date)
            {
                Log.Warning("[REMAP_DATE] {Identifier}: Remapped '{Original}' → '{Result}'",
                    identifier, date, result);
                return result;
            }
        }

        // 1970-03-XX or 1970-XX-XX which is okay because it is handled by the rebuild
        if (date.Contains('X'))
        {
            return date;
        }

        // happy case
        if (TestDate(date))
        {
            return date;
        }

        var d = TryFlippingMonthAndDate(date);

        if (d != null)
        {
            Log.Warning("[WEIRD_DATE] {Identifier}: Flipped month/day '{Original}' → '{Result}'",
                identifier, date, d);
            return d;
        }

        // try to parse it out of the identifier
        if (identifier != null)
        {
            var matches = ExtractDateFromIdentifier.Match(identifier);

            if (matches.Success)
            {
                var tdate = matches.Groups[1].Value;

                if (TestDate(tdate))
                {
                    Log.Warning("[WEIRD_DATE] {Identifier}: Extracted date from identifier, metadata date '{MetadataDate}' was invalid, using '{Result}'",
                        identifier, date, tdate);
                    return tdate;
                }

                var flipped = TryFlippingMonthAndDate(tdate);

                if (flipped != null)
                {
                    Log.Warning("[WEIRD_DATE] {Identifier}: Extracted and flipped date from identifier, metadata date '{MetadataDate}' was invalid, using '{Result}'",
                        identifier, date, flipped);
                    return flipped;
                }
            }
        }

        Log.Error("[WEIRD_DATE] {Identifier}: Unrecoverable date '{Date}' - all parsing strategies failed",
            identifier, date);
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
