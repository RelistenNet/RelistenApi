using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Relisten.Kb.Titles;

/// What a part of a title looks like before anyone decides which song it is.
public enum PartHint
{
    /// Text that may name a song.
    Song,

    /// Tuning, banter, crowd, set and encore breaks: very likely not a song.
    Noise,
}

/// One song-sized piece of a track title: "Scarlet Begonias >" is one part with SegueAfter.
/// Key is the normalised text (TitleNormalizer) every lookup and grouping uses.
public sealed record TitlePart(
    string Text,
    string Key,
    IReadOnlyList<string> Qualifiers,
    bool SegueAfter,
    PartHint Hint
);

/// A parsed track title. Parts is empty when the title carries no song text at all (a bare
/// file name such as "wsp2007-07-13d2t02"); such tracks can only be labelled through other
/// sources of the same show.
public sealed record ParsedTitle(string Raw, IReadOnlyList<TitlePart> Parts)
{
    public bool HasText => Parts.Count > 0;
}

/// The deterministic first stage of title matching (string-theory docs/matching.md T1 step 1):
/// strips file names, identifiers, track numbers, footnote markers and durations; splits on
/// segue markers; and lifts known qualifiers out of parentheses. Parentheses it doesn't know
/// stay in the text, because some are part of the name ("Axilla (Part II)").
///
/// Version is part of every claim's author_ref that depends on the parse.
public static partial class TitleParser
{
    public const string Version = "title-parse/1";

    private static readonly Dictionary<string, string> QualifierWords = new()
    {
        ["cont"] = "continued",
        ["contd"] = "continued",
        ["continued"] = "continued",
        ["reprise"] = "reprise",
        ["rep"] = "reprise",
        ["partial"] = "partial",
        ["cut"] = "partial",
        ["fade"] = "partial",
        ["fade in"] = "partial",
        ["fade out"] = "partial",
        ["fades in"] = "partial",
        ["fades out"] = "partial",
        ["incomplete"] = "partial",
        ["fragment"] = "partial",
        ["tape cut"] = "partial",
        ["tape flip"] = "tape_flip",
        ["tape change"] = "tape_flip",
        ["flip"] = "tape_flip",
        ["acoustic"] = "acoustic",
        ["instrumental"] = "instrumental",
        ["unfinished"] = "unfinished",
    };

    private static readonly HashSet<string> NoiseWords =
    [
        "tuning",
        "tune",
        "tuneup",
        "banter",
        "talk",
        "stage",
        "announcement",
        "announcements",
        "crowd",
        "applause",
        "noise",
        "dead",
        "air",
        "silence",
        "filler",
        "soundcheck",
        "set",
        "break",
        "setbreak",
        "encore",
        "and",
        "with",
        "band",
        "intros",
        "introductions",
        "tape",
        "flip",
        "change",
    ];

    private static readonly HashSet<string> SetWords =
    [
        "set",
        "encore",
        "break",
        "and",
        "with",
        "band",
        "tape",
    ];

    public static ParsedTitle Parse(string raw, int? trackPosition = null)
    {
        var fromFileName = FileExtension().IsMatch(raw.Trim());
        var text = StripFileName(raw.Trim());
        text = StripIdentifier(text);
        text = StripTrackNumber(text, trackPosition);
        text = EncorePrefix().Replace(text, "");

        // Pieces with the separator that follows each: an arrow is a segue, a slash only says
        // two songs share the track.
        var pieces = new List<(string Text, bool SegueAfter)>();
        var at = 0;
        foreach (Match separator in SegueSplit().Matches(text))
        {
            pieces.Add((text[at..separator.Index], !separator.Value.Contains('/')));
            at = separator.Index + separator.Length;
        }

        pieces.Add((text[at..], false));

        var parts = new List<TitlePart>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var part = ParsePart(pieces[i].Text, fromFileName);
            if (part == null)
            {
                // A trailing marker ("Stella Blue ->", "RV > *") means the last real part
                // segues on.
                if (parts.Count > 0 && i == pieces.Count - 1 && pieces[i - 1].SegueAfter)
                {
                    parts[^1] = parts[^1] with { SegueAfter = true };
                }

                continue;
            }

            parts.Add(part with { SegueAfter = pieces[i].SegueAfter });
        }

        return new ParsedTitle(raw, parts);
    }

    private static string StripFileName(string text)
    {
        if (!FileExtension().IsMatch(text))
        {
            return text;
        }

        text = FileExtension().Replace(text, "");
        var slash = text.LastIndexOf('/');
        if (slash >= 0 && !text[slash..].Contains(' '))
        {
            text = text[(slash + 1)..];
        }

        // Archive-style file names: "1979-04-23 - Venue - City (FM) - 101 Intro".
        var fields = text.Split(" - ");
        if (fields.Length >= 3 && LeadingDate().IsMatch(fields[0]))
        {
            text = DiscTrackCode().Replace(fields[^1].Trim(), "");
        }

        return text;
    }

    private static string StripIdentifier(string text)
    {
        var stripped = Identifier().Replace(text, "");
        return stripped.Length == text.Length ? text : stripped.TrimStart(' ', '-', '_', '.');
    }

    private static string StripTrackNumber(string text, int? trackPosition)
    {
        var punctuated = PunctuatedTrackNumber().Match(text);
        if (punctuated.Success)
        {
            return text[punctuated.Length..];
        }

        var bare = BareTrackNumber().Match(text);
        if (bare.Success && trackPosition is { } p && int.Parse(bare.Groups[1].Value) == p)
        {
            return text[bare.Length..];
        }

        return text;
    }

    private static TitlePart? ParsePart(string piece, bool fromFileName)
    {
        var qualifiers = new List<string>();
        var text = Parenthetical()
            .Replace(
                piece,
                m =>
                {
                    var inner = TitleNormalizer.Normalize(m.Groups["inner"].Value);
                    if (Duration().IsMatch(m.Groups["inner"].Value.Trim()))
                    {
                        return " ";
                    }

                    if (QualifierWords.TryGetValue(inner, out var qualifier))
                    {
                        qualifiers.Add(qualifier);
                        return " ";
                    }

                    if (inner.StartsWith("w ") || inner.StartsWith("with "))
                    {
                        qualifiers.Add("with_guest");
                        return " ";
                    }

                    return m.Value;
                }
            )
            .Trim();

        // A part that is only a bracket ("[I didn't fuck up today]") is its own text.
        var whole = WholeBracket().Match(text);
        if (whole.Success)
        {
            text = whole.Groups["inner"].Value;
        }

        text = Markers().Replace(text, "").Trim(' ', '-', '_', '.', ',', ':', ';');
        var key = TitleNormalizer.Normalize(text);
        if (key.Length == 0 || LooksLikeFileName(text, fromFileName))
        {
            return null;
        }

        return new TitlePart(text, key, qualifiers.Distinct().ToList(), false, HintFor(key));
    }

    private static PartHint HintFor(string key)
    {
        var tokens = key.Split(' ');
        var noise =
            tokens.All(t => NoiseWords.Contains(t) || t.All(char.IsDigit))
            && tokens.Any(t => !SetWords.Contains(t) && !t.All(char.IsDigit));
        return noise ? PartHint.Noise : PartHint.Song;
    }

    /// A single token mixing letters and digits left after stripping a file name ("tuckab11"
    /// from tuckab11.mp3), or carrying a long digit run. Short ones are real names: "NO2",
    /// "1x1", "LL3".
    private static bool LooksLikeFileName(string text, bool fromFileName) =>
        !text.Contains(' ')
        && text.Any(char.IsDigit)
        && text.Any(char.IsLetter)
        && (fromFileName || text.Count(char.IsDigit) >= 4);

    [GeneratedRegex(@"\.(mp3|flac|shn|wav|ogg|m4a|aiff?)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex LeadingDate();

    [GeneratedRegex(@"^[1-9]\d{2}\s+")]
    private static partial Regex DiscTrackCode();

    // A taper's file identifier: optional letters, a date, then disc/set/track codes, up to a
    // separator: "wsp2007-04-14set2t05-", "gd83-03-27 s2t08 ", "Abear_2023-09-28_S202".
    [GeneratedRegex(
        @"^[a-z_]*(?:\d{4}|\d{2})[-._]?\d{2}[-._]?\d{2}[a-z0-9._]*(?:\s+(?:[sd]\d+t\d+|t\d+)\b)?(?=[\s\-]|$)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^(?:d|cd|disc|s|set)?\s*\d{1,3}\s*(?:[.)\]:_-]|t\d+)\s*")]
    private static partial Regex PunctuatedTrackNumber();

    [GeneratedRegex(@"^(\d{1,3})\s+(?=\S)")]
    private static partial Regex BareTrackNumber();

    [GeneratedRegex(@"^(?:encore|enc|e)\s*[:.]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex EncorePrefix();

    // Segue markers: "->", "-->", ">", "→", "»", and a slash between words ("Drums/Space",
    // "crowd / tuning") but not inside a name ("AC/DC Bag", "w/ Branford").
    [GeneratedRegex(@"\s*(?:-+>|>+|→|»)\s*|(?<=\p{L}{3})/(?=\p{L}{3})|\s+/\s+")]
    private static partial Regex SegueSplit();

    [GeneratedRegex(@"[\(\[](?<inner>[^\)\]]*)[\)\]]")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"^[\(\[](?<inner>[^\)\]]*)[\)\]]$")]
    private static partial Regex WholeBracket();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex Duration();

    // Footnote markers taper notes refer to: "Drive*", "Magic Carpet Ride*#", "bearsong **".
    [GeneratedRegex(@"[\*#%\^~@†‡]+")]
    private static partial Regex Markers();
}
