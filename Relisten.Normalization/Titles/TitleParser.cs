using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Titles;

/// What a part of a title looks like before anyone decides which song it is.
public enum PartHint
{
    /// Text that may name a song.
    Song,

    /// Tuning, banter, crowd, set and encore breaks: very likely not a song.
    Noise,
}

/// One song-sized piece of a track title: "Scarlet Begonias >" is one part with SegueAfter.
/// Key is the normalised song name (TitleNormalizer) every lookup and grouping uses. Notes
/// are what the title said about the recording ("aud -> sbd", "John Hartford cover"), never
/// part of the name. Bare is the key without a trailing qualifier word ("plane crash" for
/// "Plane Crash reprise"), tried only when the key itself names no song, so a band whose
/// reprise is its own song ("Tweezer Reprise") keeps it.
public sealed record TitlePart(
    string Text,
    string Key,
    IReadOnlyList<string> Qualifiers,
    bool SegueAfter,
    PartHint Hint
)
{
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? Bare { get; init; }
    public string? BareQualifier { get; init; }
}

/// A parsed track title. Parts is empty when the title carries no song text at all (a bare
/// file name such as "wsp2007-07-13d2t02"); such tracks can only be labelled through other
/// sources of the same show.
public sealed record ParsedTitle(string Raw, IReadOnlyList<TitlePart> Parts)
{
    public bool HasText => Parts.Count > 0;
}

/// The deterministic first stage of title matching (string-theory docs/matching.md T1 step 1):
/// cuts a title down to song names. It strips file names, identifiers, track numbers,
/// footnote markers and durations; splits on segue markers outside brackets; lifts known
/// qualifiers out of parentheses; and moves recording notes (source changes, cover credits,
/// "sbd") into Notes. Other parentheses stay in the text, because some are part of the name
/// ("Axilla (Part II)").
///
/// Version is part of every claim's author_ref that depends on the parse.
public static partial class TitleParser
{
    public const string Version = "title-parse/2";

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
        ["jam"] = "jam",
        // "(Check)": played at soundcheck, the same day but not part of the show.
        ["check"] = "soundcheck",
        ["soundcheck"] = "soundcheck",
        ["sound check"] = "soundcheck",
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
        "introduction",
        "audience",
        "untitled",
        "tape",
        "flip",
        "change",
    ];

    // Words that describe the recording, not the song: "(aud -> sbd)", "Not Fade Away SBB".
    private static readonly HashSet<string> RecordingWords =
    [
        "aud",
        "sbd",
        "sbb",
        "matrix",
        "mtx",
        "fm",
        "dat",
        "source",
        "patch",
        "patched",
        "soundboard",
        "audience",
        "cassette",
        "master",
    ];

    // A trailing word that may be a qualifier rather than part of the name.
    private static readonly Dictionary<string, string> TrailingQualifiers = new()
    {
        ["reprise"] = "reprise",
        ["tease"] = "tease",
        ["intro"] = "intro",
        ["outro"] = "outro",
        // A jam on the song ("Amazing Grace Jam"); a named jam that is its own song ("Mind
        // Left Body Jam") matches by its exact name first.
        ["jam"] = "jam",
    };

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
        text = StripTrackNumber(text, trackPosition, fromFileName);
        text = EncorePrefix().Replace(text, "");
        // "(>)" is a segue marker in brackets; any other arrow in brackets is a note.
        text = BracketedSegue().Replace(text, " > ");

        // Pieces with the separator that follows each: an arrow is a segue, a slash only says
        // two songs share the track. Separators inside brackets don't split: "(aud -> sbd)".
        var depth = new int[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            depth[i + 1] =
                depth[i]
                + text[i] switch
                {
                    '(' or '[' => 1,
                    ')' or ']' => depth[i] > 0 ? -1 : 0,
                    _ => 0,
                };
        }

        var pieces = new List<(string Text, bool SegueAfter)>();
        var at = 0;
        foreach (
            Match separator in SegueSplit()
                .Matches(text)
                .Where(m => depth[m.Index] == 0 && depth[m.Index + m.Length] == 0)
        )
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

            parts.Add(part with { SegueAfter = pieces[i].SegueAfter || part.SegueAfter });
        }

        // A soundcheck covers the whole track: "(Check) Jam > Leaves" is a soundcheck medley.
        if (parts.Any(p => p.Qualifiers.Contains("soundcheck")))
        {
            parts = parts
                .Select(p =>
                    p.Qualifiers.Contains("soundcheck")
                        ? p
                        : p with
                        {
                            Qualifiers = [.. p.Qualifiers, "soundcheck"],
                        }
                )
                .ToList();
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

    private static string StripTrackNumber(string text, int? trackPosition, bool fromFileName)
    {
        // A time before the name ("05:12 - Nebraska", "4:43) Nebraska") is a duration or a
        // cue; "5:15" alone is a name.
        text = DurationPrefix().Replace(text, "");
        var punctuated = PunctuatedTrackNumber().Match(text);
        if (punctuated.Success)
        {
            return text[punctuated.Length..];
        }

        var bare = BareTrackNumber().Match(text);
        // A bare number is a track number when it matches the position, or when the title is
        // a file name ("08 Sugaree.mp3"); otherwise it may be the name ("75 and Sunny").
        if (
            bare.Success
            && (fromFileName || trackPosition is { } p && int.Parse(bare.Groups[1].Value) == p)
        )
        {
            return text[bare.Length..];
        }

        return text;
    }

    private static TitlePart? ParsePart(string piece, bool fromFileName)
    {
        var qualifiers = new List<string>();
        var notes = new List<string>();
        var text = Parenthetical()
            .Replace(
                piece,
                m =>
                {
                    var raw = m.Groups["inner"].Value.Trim();
                    var inner = TitleNormalizer.Normalize(raw);
                    if (Duration().IsMatch(raw))
                    {
                        return " ";
                    }

                    if (QualifierWords.TryGetValue(inner, out var qualifier))
                    {
                        qualifiers.Add(qualifier);
                        return " ";
                    }

                    // Notes about the recording, never the name: a source change, a cover
                    // credit, a tease, a bare count ("Ol' Rickard (2)").
                    // A bracket that is the whole part is its text (below), not a note.
                    var words = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (
                        m.Value != piece.Trim()
                        && (
                            raw.Contains('>')
                            || raw.Contains('→')
                            || words.Any(RecordingWords.Contains)
                            || words.Contains("cover")
                            || words.Any(w => w is "tease" or "teases" or "quote")
                            || words.Length > 0 && words.All(w => w.All(char.IsDigit))
                        )
                    )
                    {
                        notes.Add(raw);
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

        // A part that is only a bracket ("[I didn't fuck up today]") is its own text, and may
        // end in its own segue ("(Second Cousins? >)").
        var segue = false;
        var whole = WholeBracket().Match(text);
        if (whole.Success)
        {
            // A bracket holding only a count or a qualifier ("Space > (2)", "(Fade In)")
            // names no song.
            var inner = TitleNormalizer.Normalize(whole.Groups["inner"].Value);
            if (inner.All(c => char.IsDigit(c) || c == ' ') || QualifierWords.ContainsKey(inner))
            {
                return null;
            }

            text = whole.Groups["inner"].Value;
            var trailing = TrailingSegue().Match(text);
            if (trailing.Success)
            {
                segue = true;
                text = text[..trailing.Index];
            }
        }

        text = Markers().Replace(text, "");
        text = TrailingPlus().Replace(text, "").Trim(' ', '-', '_', '.', ',', ':', ';');
        var recording = TrailingRecordingWords().Match(text);
        if (recording.Success && recording.Index > 0)
        {
            notes.Add(recording.Value.Trim());
            text = text[..recording.Index].Trim(' ', '-', '_', '.', ',', ':', ';');
        }

        text = SetPrefix().Replace(text, "");
        text = TrailingFileCode().Replace(text, "").Trim(' ', '-', '_', '.', ',', ':', ';');
        var key = TitleNormalizer.Normalize(text);
        if (key.Length == 0 || LooksLikeFileName(text, fromFileName) || GenericLabel().IsMatch(key))
        {
            return null;
        }

        string? bare = null;
        string? bareQualifier = null;
        var lastSpace = key.LastIndexOf(' ');
        if (lastSpace > 0 && TrailingQualifiers.TryGetValue(key[(lastSpace + 1)..], out var word))
        {
            bare = key[..lastSpace];
            bareQualifier = word;
        }

        return new TitlePart(text, key, qualifiers.Distinct().ToList(), segue, HintFor(key))
        {
            Notes = notes,
            Bare = bare,
            BareQualifier = bareQualifier,
        };
    }

    private static PartHint HintFor(string key)
    {
        var tokens = key.Split(' ');
        // A title that is only "Encore" is the encore break, not a song.
        var noise =
            key == "encore"
            || tokens.All(t => NoiseWords.Contains(t) || t.All(char.IsDigit))
                && tokens.Any(t => !SetWords.Contains(t) && !t.All(char.IsDigit));
        return noise ? PartHint.Noise : PartHint.Song;
    }

    /// A single token mixing letters and digits left after stripping a file name ("tuckab11"
    /// from tuckab11.mp3), or carrying a long digit run. Short ones are real names, file name
    /// or not: "NO2", "1x1", "LL3" ("08 NO2.mp3").
    private static bool LooksLikeFileName(string text, bool fromFileName) =>
        !text.Contains(' ')
        && text.Any(char.IsDigit)
        && text.Any(char.IsLetter)
        && (fromFileName && text.Length >= 5 || text.Count(char.IsDigit) >= 4);

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

    [GeneratedRegex(@"^(?:d|cd|disc|s|set)?\s*\d{1,3}\s*(?:[.)\]_-]|:(?!\d)|t\d+)\s*")]
    private static partial Regex PunctuatedTrackNumber();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(?::\d{2})?\s*[-)\]]\s*")]
    private static partial Regex DurationPrefix();

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

    [GeneratedRegex(@"[\(\[]\s*(?:-+>|>+|→)\s*[\)\]]")]
    private static partial Regex BracketedSegue();

    [GeneratedRegex(@"\s*(?:-+>|>+|→)\s*$")]
    private static partial Regex TrailingSegue();

    [GeneratedRegex(@"\s*\++\s*$")]
    private static partial Regex TrailingPlus();

    // A recorder's or player's label, not a name: "Track 3", "Marker 8", "Chapter 2".
    [GeneratedRegex(@"^(?:track|marker|chapter|index|cue|title)\s*\d+$")]
    private static partial Regex GenericLabel();

    [GeneratedRegex(@"(?:\s+(?:sbd|aud|sbb|matrix|mtx|fm))+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingRecordingWords();

    [GeneratedRegex(@"^[\(\[](?<inner>[^\)\]]*)[\)\]]$")]
    private static partial Regex WholeBracket();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex Duration();

    // "Set 2- Crazy Fingers", "Set II: Bertha": the set, not the song.
    [GeneratedRegex(@"^(?:set|disc|cd)\s*(?:\d+|[ivx]+)\s*[-:.]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SetPrefix();

    // A recorder's file code after the name: "Cassidy 080925_201809_TrMic".
    [GeneratedRegex(@"(?<=\p{L}.*)\s+\S*\d{4,}\S*_\S*$|(?<=\p{L}.*)\s+\S*_\S*\d{4,}\S*$")]
    private static partial Regex TrailingFileCode();

    // Footnote markers taper notes refer to: "Drive*", "Magic Carpet Ride*#", "bearsong **".
    [GeneratedRegex(@"[\*#%\^~@†‡]+")]
    private static partial Regex Markers();
}
