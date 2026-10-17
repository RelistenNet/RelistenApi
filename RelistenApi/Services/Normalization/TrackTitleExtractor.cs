using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Extracts content mentions from file titles without assigning repertoire,
/// composition or performance identities. Only explicit title syntax is removed;
/// the retained title and exact evidence remain available for later correction.
/// </summary>
public sealed class TrackTitleExtractor
{
    public const string Version = "track-title-v1";

    private static readonly Regex FilenameMetadata = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}) - .+? - .+? - \d{3} (?<content>.+)\.(?:mp3|flac|wav|shn|ogg|m4a)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex AudioExtension = new(
        @"\.(?:mp3|flac|wav|shn|ogg|m4a)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex TrackNumber = new(
        @"^(?:0\d[.)]?|\d{1,3}[.)])\s+",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex OpaqueFilename = new(
        @"(?:\d{4}[-_.]\d{2}[-_.]\d{2}.*t\d+$)|^(?:(?:disc|d)\s*\d+\s*)?(?:track|t)\s*\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex Separator = new(@"-?>+|→|\s+/\s+", RegexOptions.CultureInvariant);

    private static readonly Regex PartialSuffix = new(
        @"\s*\((?:partial|cut|fragment)\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>
    /// Returns artist-scoped, title-derived mentions and syntax evidence. A false
    /// IsResolved means at least one passage still needs interpretation; supported
    /// mentions in the same file are retained. This method never changes show/date
    /// assignment and supplies no inferred audio times or occurrence identities.
    /// </summary>
    public TrackTitleExtraction Extract(Guid artistUuid, string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var qualifiers = new List<TrackTitleQualifier>();
        var (start, end) = ContentRange(title, qualifiers);
        var content = title[start..end];
        if (content.Length == 0 || OpaqueFilename.IsMatch(content))
        {
            return new(
                artistUuid,
                title,
                [],
                TrackTitleSequence.Unresolved,
                qualifiers,
                false,
                "no_song_title_identified"
            );
        }

        var delimiters = Separator.Matches(content).Cast<Match>().ToList();
        // Unspaced slashes in ordinary names (for example AC/DC) are not separators.
        // Known content labels can be distinguished, but their slash gives no order.
        var mixedLabels = false;
        if (delimiters.Count == 0 && content.Contains('/'))
        {
            var labels = content.Split('/');
            if (labels.All(label => Classify(label.Trim()) != TrackContentKind.Song))
            {
                delimiters = Regex.Matches(content, "/").Cast<Match>().ToList();
                mixedLabels = true;
            }
        }

        var segments = new List<TrackTitleSegment>();
        var next = start;
        foreach (var delimiter in delimiters)
        {
            var delimiterStart = start + delimiter.Index;
            var transition =
                delimiter.Value.Contains('>') || delimiter.Value == "→"
                    ? TitleTransition.SegueMarker
                    : TitleTransition.Unknown;
            AddSegment(title, next, delimiterStart, transition, segments, qualifiers);
            qualifiers.Add(
                new("separator", Evidence(title, delimiterStart, delimiterStart + delimiter.Length))
            );
            next = delimiterStart + delimiter.Length;
        }

        AddSegment(title, next, end, TitleTransition.Unknown, segments, qualifiers);
        var unresolved =
            segments.Count == 0
            || segments.Any(segment => segment.Kind == TrackContentKind.Unknown);
        var reason = unresolved ? UnknownReason(segments) : null;
        var sequence =
            mixedLabels || segments.Count == 0 ? TrackTitleSequence.Unresolved
            : segments.Count == 1 ? TrackTitleSequence.Single
            : TrackTitleSequence.TitleOrder;

        return new(artistUuid, title, segments, sequence, qualifiers, !unresolved, reason);
    }

    private static (int Start, int End) ContentRange(
        string title,
        List<TrackTitleQualifier> qualifiers
    )
    {
        var (start, end) = Trim(title, 0, title.Length);
        var filename = FilenameMetadata.Match(title[start..end]);
        if (
            filename.Success
            && DateOnly.TryParseExact(
                filename.Groups["date"].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _
            )
        )
        {
            var content = filename.Groups["content"];
            var contentStart = start + content.Index;
            var contentEnd = contentStart + content.Length;
            qualifiers.Add(new("filename_metadata", Evidence(title, start, contentStart)));
            qualifiers.Add(new("audio_extension", Evidence(title, contentEnd, end)));
            return (contentStart, contentEnd);
        }

        var extension = AudioExtension.Match(title[start..end]);
        if (extension.Success)
        {
            qualifiers.Add(new("audio_extension", Evidence(title, start + extension.Index, end)));
            end = start + extension.Index;
        }

        var number = TrackNumber.Match(title[start..end]);
        if (number.Success)
        {
            qualifiers.Add(new("track_number", Evidence(title, start, start + number.Length)));
            start += number.Length;
        }

        return Trim(title, start, end);
    }

    private static void AddSegment(
        string raw,
        int start,
        int end,
        TitleTransition transition,
        List<TrackTitleSegment> segments,
        List<TrackTitleQualifier> qualifiers
    )
    {
        (start, end) = Trim(raw, start, end);
        if (start == end)
        {
            return;
        }

        var coverage = TitleCoverage.Unknown;
        var partial = PartialSuffix.Match(raw[start..end]);
        if (partial.Success)
        {
            qualifiers.Add(new("partial_coverage", Evidence(raw, start + partial.Index, end)));
            end = start + partial.Index;
            coverage = TitleCoverage.Partial;
        }

        if (raw.AsSpan(start, end - start).StartsWith("...") || raw[start] == '…')
        {
            var markerEnd = start + (raw[start] == '…' ? 1 : 3);
            qualifiers.Add(new("partial_marker", Evidence(raw, start, markerEnd)));
            start = markerEnd;
            coverage = TitleCoverage.PartialMarker;
        }

        (start, end) = Trim(raw, start, end);
        if (start == end)
        {
            return;
        }

        var text = raw[start..end];
        var kind = Classify(text);
        segments.Add(
            new(
                segments.Count + 1,
                kind,
                kind == TrackContentKind.Song ? text : null,
                Evidence(raw, start, end),
                coverage,
                transition
            )
        );
    }

    private static TrackContentKind Classify(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "jam" => TrackContentKind.Jam,
            "banter" or "stage banter" or "talk" or "announcements" => TrackContentKind.Speech,
            "tuning" or "crowd" or "applause" or "crowd & tuning" or "tuning & crowd" =>
                TrackContentKind.Nonmusic,
            "intro"
            or "intros"
            or "introduction"
            or "introductions"
            or "unknown"
            or "(unknown)"
            or "?"
            or "??" => TrackContentKind.Unknown,
            _ when Regex.IsMatch(
                    text,
                    @"^\d+\s+\(unknown\)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                ) => TrackContentKind.Unknown,
            _ => TrackContentKind.Song,
        };
    }

    private static string UnknownReason(IReadOnlyList<TrackTitleSegment> segments)
    {
        var introduction = segments.FirstOrDefault(segment =>
            segment.Kind == TrackContentKind.Unknown
            && segment.Evidence.Quote.StartsWith("intro", StringComparison.OrdinalIgnoreCase)
        );
        if (introduction == null)
        {
            return "unknown_song";
        }

        return introduction.Evidence.Quote.Equals("intros", StringComparison.OrdinalIgnoreCase)
            ? "intros_does_not_distinguish_music_from_spoken_introduction"
            : "intro_does_not_distinguish_music_from_spoken_introduction";
    }

    private static (int Start, int End) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }

    private static NormalizationTextEvidence Evidence(string raw, int start, int end)
    {
        return new(
            "title",
            "/title",
            raw[start..end],
            raw[..start].EnumerateRunes().Count(),
            raw[..end].EnumerateRunes().Count()
        );
    }
}
