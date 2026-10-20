using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class TrackTitleExtractor
{
    private static readonly Regex FilenameMetadata = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}) - .+? - .+? - \d{3} (?<content>.+)\.(?:mp3|flac|wav|shn|ogg|m4a)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex AudioExtension = new(
        @"\.(?:mp3|flac|wav|shn|ogg|m4a)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex DatedTrackFilename = new(
        @"^(?<date>\d{4}[-_.]\d{2}[-_.]\d{2})(?:[ _-]*d\d+t\d+|[ _-]+(?:track\s*\d+|\d{1,3}[.)]?))[ _.-]+(?<content>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex RecordingTrackFilename = new(
        @"^(?:[a-z]{1,8})?(?<date>(?:\d{4}|\d{2})[-_.]\d{2}[-_.]\d{2})(?:[ _-]*(?:d|s)\d+[ _.-]*t\d+|[ _-]*(?:(?:set|disc)\s*\d+[ ._-]+)?(?:track|t)\s*\d+[.)]?|[ _-]+\d{1,3}[.)]?)[ _.-]+(?<content>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex TrackNumber = new(
        @"^(?:\d{1,3}[.)](?!\d)\s*|0\d\s+)",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex LeadingSet = new(
        @"^(?:\(set\s*\d{1,2}\)|\[set\s*\d{1,2}\])\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex StructuralSuffix = new(
        @"\s*(?:\((?<label>partial|cut|fragment|aborted|filler)\)|\[(?<label>partial|cut|fragment|aborted|filler)\]|\((?<set>set\s*\d{1,2})\)|\[(?<set>set\s*\d{1,2})\]|\((?<duration>(?:\d{1,2}:)?[0-5]?\d:[0-5]\d)\)|\[(?<duration>(?:\d{1,2}:)?[0-5]?\d:[0-5]\d)\])\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex MusicalVariantSuffix = new(
        @"\s+(?<marker>(?<variant>jam|tease|reprise|practice)|\((?<variant>jam|tease|reprise|practice)\)|\[(?<variant>jam|tease|reprise|practice)\])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static (int Start, int End) ContentRange(
        string title,
        List<TrackTitleQualifier> qualifiers
    )
    {
        var (start, end) = Trim(title, 0, title.Length);
        var filename = FilenameMetadata.Match(title[start..end]);
        if (filename.Success && ValidFilenameDate(filename.Groups["date"].Value))
        {
            var content = filename.Groups["content"];
            var contentStart = start + content.Index;
            var contentEnd = contentStart + content.Length;
            qualifiers.Add(
                Qualifier(
                    "filename_metadata",
                    Evidence(title, start, contentStart),
                    TrackTitleStructuralKind.FilenameMetadata
                )
            );
            qualifiers.Add(
                Qualifier(
                    "audio_extension",
                    Evidence(title, contentEnd, end),
                    TrackTitleStructuralKind.AudioExtension
                )
            );
            start = contentStart;
            end = contentEnd;
        }
        else
        {
            var extension = AudioExtension.Match(title[start..end]);
            if (extension.Success)
            {
                qualifiers.Add(
                    Qualifier(
                        "audio_extension",
                        Evidence(title, start + extension.Index, end),
                        TrackTitleStructuralKind.AudioExtension
                    )
                );
                end = start + extension.Index;
            }

            var recording = RecordingTrackFilename.Match(title[start..end]);
            var dated = recording.Success ? recording : DatedTrackFilename.Match(title[start..end]);
            if (dated.Success && ValidFilenameDate(dated.Groups["date"].Value))
            {
                var contentStart = start + dated.Groups["content"].Index;
                qualifiers.Add(
                    Qualifier(
                        "filename_metadata",
                        Evidence(title, start, contentStart),
                        TrackTitleStructuralKind.FilenameMetadata
                    )
                );
                start = contentStart;
            }
        }

        // Only explicit prefix tokens are peeled. Bare numbers can be song names.
        while (start < end)
        {
            var content = title[start..end];
            var number = TrackNumber.Match(content);
            var set = LeadingSet.Match(content);
            var prefix = number.Success ? number : set;
            if (!prefix.Success)
            {
                break;
            }

            qualifiers.Add(
                Qualifier(
                    number.Success ? "track_number" : "set_label",
                    Evidence(title, start, start + prefix.Length),
                    number.Success
                        ? TrackTitleStructuralKind.TrackNumber
                        : TrackTitleStructuralKind.SetLabel
                )
            );
            start += prefix.Length;
        }

        return Trim(title, start, end);
    }

    private static bool ValidFilenameDate(string date)
    {
        var calendarDate = date.Replace('_', '-').Replace('.', '-');
        if (calendarDate.Length == 8)
        {
            // A short year is checked only for calendar syntax. This does not assign
            // a performance date or assert which century the recording belongs to.
            calendarDate = "20" + calendarDate;
        }

        return DateOnly.TryParseExact(
            calendarDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _
        );
    }

    private static (int Start, int End) SegmentContentRange(
        string raw,
        int start,
        int end,
        List<TrackTitleQualifier> qualifiers,
        ref TitleCoverage coverage
    )
    {
        while (start < end)
        {
            var suffix = StructuralSuffix.Match(raw[start..end]);
            if (!suffix.Success)
            {
                break;
            }

            var label = suffix.Groups["label"].Value.ToLowerInvariant();
            var (kind, structuralKind) = label switch
            {
                "partial" or "cut" or "fragment" => (
                    "partial_coverage",
                    TrackTitleStructuralKind.PartialCoverage
                ),
                "aborted" => ("aborted", TrackTitleStructuralKind.Aborted),
                "filler" => ("filler", TrackTitleStructuralKind.Filler),
                _ when suffix.Groups["set"].Success => (
                    "set_label",
                    TrackTitleStructuralKind.SetLabel
                ),
                _ => ("duration", TrackTitleStructuralKind.Duration),
            };
            qualifiers.Add(
                Qualifier(kind, Evidence(raw, start + suffix.Index, end), structuralKind)
            );
            if (
                structuralKind
                is TrackTitleStructuralKind.PartialCoverage
                    or TrackTitleStructuralKind.Aborted
            )
            {
                coverage = TitleCoverage.Partial;
            }

            end = start + suffix.Index;
        }

        return Trim(raw, start, end);
    }

    private static TrackTitleCoreCandidate? CoreTitle(string raw, int start, int end)
    {
        var retainedEnd = end;
        var variants = new List<TrackTitleVariant>();
        while (start < end)
        {
            var suffix = MusicalVariantSuffix.Match(raw[start..end]);
            if (!suffix.Success)
            {
                break;
            }

            var marker = suffix.Groups["marker"];
            var kind = Enum.Parse<TrackTitleVariantKind>(suffix.Groups["variant"].Value, true);
            variants.Insert(
                0,
                new(kind, Evidence(raw, start + marker.Index, start + marker.Index + marker.Length))
            );
            end = start + suffix.Index;
        }

        // This is an alternative candidate, never a rewrite of the named variant.
        // Dictionary consumers must try the complete retained title first.
        return end == retainedEnd
            ? null
            : new(raw[start..end], Evidence(raw, start, end), variants);
    }

    private static TrackTitleQualifier Qualifier(
        string kind,
        NormalizationTextEvidence evidence,
        TrackTitleStructuralKind structuralKind
    ) => new(kind, evidence) { StructuralKind = structuralKind };
}
