using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Reads explicit aliases in bounded performer-roster statements. Context is retained for
/// interpretation; this extractor creates no recording credits or participation assertions.
/// Unknown alias contexts stay unresolved, including non-people uses of the same marker.
/// </summary>
public sealed class SourceActorAliasExtractor
{
    public const string Version =
        "source-actor-aliases-v2/" + RecordingActorEvidenceExtractor.Version;

    // Bound expansion before the shared actor parser runs. Oversized text stays cited and
    // unresolved; absence of extracted aliases is never treated as a complete source clear.
    private const int MaximumFieldLength = 262_144;
    private const int MaximumLineLength = 4_096;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Heading = new(
        @"^(?:the\s+band|band(?:\s+line[- ]?up)?|line[- ]?up|personnel|musicians)\s*:\s*$",
        Options,
        Timeout
    );
    private static readonly Regex Qualifier = new(
        @"^[-–—\s]*(?:and\s+)?some\s+or\s+all\s+of\s+the\s+following[-–—\s]*$",
        Options,
        Timeout
    );
    private static readonly Regex AliasMarker = new(
        @"\b(?:aka|a\.k\.a\.?|also\s+known\s+as)\b",
        Options,
        Timeout
    );
    private static readonly Regex NumberedEntry = new(
        @"^\s*(?:d\d+t\d+|\[\d+\]|\d{1,3}[.)]|\d{1,3}\s+)",
        Options,
        Timeout
    );
    private static readonly Regex InstrumentSuffix = new(
        @"^(?:[\p{L}]+|\s|[,/&])+$",
        RegexOptions.CultureInvariant,
        Timeout
    );
    private static readonly Regex InstrumentItems = new(
        @"\s*(?:[,/&]|\band\b)\s*",
        Options,
        Timeout
    );
    private static readonly Regex ShortListItem = new(
        @"^[\p{L}\p{M}]+(?:[ -][\p{L}\p{M}]+){0,2}$",
        RegexOptions.CultureInvariant,
        Timeout
    );
    private static readonly Regex SectionLabel = new(
        @"^(?:notes?|recording\s+notes?|source(?:\s+notes?)?|setlists?|tracklists?|credits?"
            + @"|bonus(?:\s+material)?|encore|(?:set|disc|side)\s+(?:\d+|[ivx]+))$",
        Options,
        Timeout
    );
    private static readonly Regex ProsePrefix = new(
        @"^(?:(?:[\p{L}]+ly\s+)?(?:recorded|taped|transferred|mastered|uploaded|mixed)"
            + @"(?:\s+(?:and|&)\s+(?:[\p{L}]+ly\s+)?(?:recorded|taped|transferred|mastered|uploaded|mixed))*\s+by\b"
            + @"|(?:tapers?|transferr?ers?|recording\s+engineer|mastering\s+engineer"
            + @"|matrix\s+builder|uploader|collector|musicians?)\s*:"
            + @"|recording\s+(?:notes?|details?|information)\b"
            + @"|notes?\s+(?:on|about|for|from)\b"
            + @"|(?:after|before|during)\s+(?:the|this|that)\b"
            + @"|(?:some|more|additional)\s+(?:notes?|information|details?)\b"
            + @"|(?:this|that|these|those)\b)",
        Options,
        Timeout
    );
    private static readonly Regex NarrativePrefix = new(
        @"^(?:(?:the|a|an|our|their)\s+[\p{L}][\p{L}'’ -]{0,80}"
            + @"|(?:i|we|it|they|he|she))\s+(?:played|plays|performed|performs|sang|sings"
            + @"|recorded|taped|was|were|is|are|has|have|had)\b",
        Options,
        Timeout
    );
    private static readonly Regex ContinuationBoundary = new(
        @"\b(?:notes?|info(?:rmation)?|details?|setlists?|tracklists?|tracks?|songs?|credits?"
            + @"|bonus|material|encore|recording|recorded|taped|transferred|mastered|uploaded"
            + @"|played|playing|source|after|before|during|from|by|for|with|without|not|never"
            + @"|was|were|is|are|on|at|this|that|these|those)\b",
        Options,
        Timeout
    );
    private static readonly Regex PedalSteel = new(@"^pedal\s+steel$", Options, Timeout);
    private const int MaximumInstrumentItems = 16;
    private const int MaximumInstrumentItemLength = 48;

    private static readonly HashSet<string> InstrumentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "vocal",
        "vocals",
        "guitar",
        "guitars",
        "keyboard",
        "keyboards",
        "bass",
        "drums",
        "percussion",
        "piano",
        "organ",
        "banjo",
        "cello",
        "violin",
        "viola",
        "trumpet",
        "trombone",
        "saxophone",
        "sax",
        "horn",
        "horns",
        "flute",
        "piccolo",
        "whistle",
        "harmonica",
        "harp",
        "mandolin",
        "synthesizer",
        "synthesizers",
        "theremin",
        "mbira",
    };
    private static readonly HashSet<string> SuffixModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "lead",
        "backing",
        "choir",
        "harmony",
        "acoustic",
        "electric",
        "double",
        "french",
        "tin",
        "pedal",
        "steel",
        "and",
    };
    private readonly RecordingActorEvidenceExtractor actorParser = new();

    /// <summary>
    /// Extracts literal aliases from supported recording-text fields. Unclassified markers
    /// and parse limits remain unresolved and make the original field's scope incomplete.
    /// </summary>
    public SourceActorAliasExtraction<NormalizationTextEvidence> Extract(
        IReadOnlyDictionary<string, string?> fields
    )
    {
        var assertions = new List<SourceActorAliasAssertion<NormalizationTextEvidence>>();
        var unresolved = new List<ActorEvidenceUnresolved<NormalizationTextEvidence>>();
        var scopes = new List<SourceActorAliasScopeCoverage<NormalizationTextEvidence>>();
        foreach (var (field, raw) in fields)
        {
            if (!SupportsField(field))
            {
                continue;
            }

            if (raw == null)
            {
                // A missing string does not tell this pure parser that a provider cleared it.
                scopes.Add(new(field, false, "field_unavailable", []));
                continue;
            }

            ReadField(field, raw, assertions, unresolved, scopes);
        }
        return new(assertions, unresolved, scopes);
    }

    private void ReadField(
        string field,
        string raw,
        List<SourceActorAliasAssertion<NormalizationTextEvidence>> assertions,
        List<ActorEvidenceUnresolved<NormalizationTextEvidence>> unresolved,
        List<SourceActorAliasScopeCoverage<NormalizationTextEvidence>> scopes
    )
    {
        var fieldEvidence = new NormalizationTextEvidence(
            field,
            "/" + field.Replace("~", "~0").Replace("/", "~1"),
            raw,
            0,
            raw.EnumerateRunes().Count()
        );
        if (string.IsNullOrWhiteSpace(raw))
        {
            scopes.Add(new(field, false, "field_clear_semantics_unavailable", [fieldEvidence]));
            return;
        }

        if (raw.Length > MaximumFieldLength)
        {
            unresolved.Add(new("alias_field_limit", [], [fieldEvidence]));
            scopes.Add(new(field, false, "unresolved_alias_statements", [fieldEvidence]));
            return;
        }

        var fieldHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        var originalIssueCount = unresolved.Count;
        RosterContext? roster = null;
        try
        {
            foreach (var line in RecordingText.Lines(field, raw))
            {
                if (line.Text.Length > MaximumLineLength)
                {
                    roster = null;
                    unresolved.Add(new("alias_line_limit", [], [line.Evidence]));
                    continue;
                }

                if (Heading.IsMatch(line.Text))
                {
                    roster = new(line.Evidence, []);
                    continue;
                }
                if (roster != null && Qualifier.IsMatch(line.Text))
                {
                    roster.Qualifiers.Add(line.Evidence);
                    continue;
                }

                var markerCount = AliasMarker.Matches(line.Text).Count;
                if (
                    roster != null
                    && !NumberedEntry.IsMatch(line.Text)
                    && TryRosterEntry(line.Text, out var actorText, out var instrumentation)
                )
                {
                    if (instrumentation == InstrumentationSupport.Partial)
                    {
                        // An unfamiliar coordinated item does not end a clearly introduced
                        // roster. Keep its context, but emit no alias from this uncertain row.
                        unresolved.Add(
                            new("unsupported_roster_instrumentation", [], [line.Evidence])
                        );
                    }
                    else if (markerCount > 0)
                    {
                        ReadAliases(
                            line,
                            actorText,
                            roster,
                            fieldHash,
                            markerCount,
                            assertions,
                            unresolved
                        );
                    }
                    continue;
                }

                // A prose/setlist/unknown line closes the roster. A marker by itself supplies
                // neither actor type nor the source statement needed to attach an alias.
                roster = null;
                if (markerCount > 0)
                {
                    unresolved.Add(new("unsupported_alias_context", [], [line.Evidence]));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            unresolved.Add(new("alias_parse_timeout", [], [fieldEvidence]));
        }

        var complete = unresolved.Count == originalIssueCount;
        scopes.Add(
            new(
                field,
                complete,
                complete ? "complete_supported_field_scan" : "unresolved_alias_statements",
                [fieldEvidence]
            )
        );
    }

    private void ReadAliases(
        RecordingText.Line line,
        string actorText,
        RosterContext roster,
        string fieldHash,
        int markerCount,
        List<SourceActorAliasAssertion<NormalizationTextEvidence>> assertions,
        List<ActorEvidenceUnresolved<NormalizationTextEvidence>> unresolved
    )
    {
        var phrases = actorParser.ReadAliasPhrases(actorText, line.Evidence, 0);
        var retainedMarkers = 0;
        foreach (var phrase in phrases)
        {
            if (!UsableName(phrase.Name) || phrase.Aliases.Any(alias => !UsableName(alias.Wording)))
            {
                unresolved.Add(
                    new("unsupported_alias_name", [phrase.ActorReferenceUuid], [line.Evidence])
                );
                continue;
            }
            retainedMarkers += phrase.Aliases.Count;
            var assertion = new SourceActorAliasAssertion<NormalizationTextEvidence>(
                Version,
                AssertionUuid(fieldHash, phrase.ActorReferenceUuid),
                phrase.ActorReferenceUuid,
                fieldHash,
                phrase.Name,
                phrase.NameEvidence,
                phrase.Aliases,
                phrase.Relationships,
                new(roster.Heading, roster.Qualifiers.ToArray(), line.Evidence),
                phrase.Unresolved
            );
            assertions.Add(assertion);
            unresolved.AddRange(phrase.Unresolved);
        }

        if (retainedMarkers != markerCount)
        {
            unresolved.Add(new("unresolved_alias_boundary", [], [line.Evidence]));
        }
    }

    private static bool TryRosterEntry(
        string line,
        out string actorText,
        out InstrumentationSupport instrumentation
    )
    {
        var delimiter = RosterDelimiter(line);
        actorText = delimiter < 0 ? "" : line[..delimiter].Trim();
        instrumentation = InstrumentationSupport.Unsupported;
        if (
            delimiter < 0
            || actorText.Length == 0
            || SectionLabel.IsMatch(actorText)
            || ProsePrefix.IsMatch(actorText)
            || IsNarrativePrefix(actorText)
        )
        {
            return false;
        }

        // A rejected first structural delimiter cannot be rescued by treating later prose
        // as an actor name. Dashes inside quoted or parenthesized names are not delimiters.
        var suffix = line[(delimiter + 1)..].Trim();
        if (!InstrumentSuffix.IsMatch(suffix))
        {
            return false;
        }

        instrumentation = ClassifyInstrumentation(suffix);
        return instrumentation == InstrumentationSupport.Supported
            || instrumentation == InstrumentationSupport.Partial
                && UsableName(actorText)
                && !ContinuationBoundary.IsMatch(suffix);
    }

    private static bool IsNarrativePrefix(string actors)
    {
        // Alias wording is literal name evidence. A finite clause before its explicit marker
        // can establish a prose boundary; words inside the alias cannot establish that boundary.
        var marker = AliasMarker.Match(actors);
        var primary = marker.Success ? actors[..marker.Index].TrimEnd() : actors;
        return NarrativePrefix.IsMatch(primary);
    }

    private static int RosterDelimiter(string line)
    {
        char? quoteEnd = null;
        var parentheses = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (quoteEnd is { } closing)
            {
                if (
                    current == closing
                    && (index == 0 || line[index - 1] != '\\')
                    && (
                        closing != '\''
                        || index + 1 == line.Length
                        || !char.IsLetter(line[index + 1])
                    )
                )
                {
                    quoteEnd = null;
                }
                continue;
            }

            if (
                (current is '"' or '“')
                || current == '\''
                    && (index == 0 || char.IsWhiteSpace(line[index - 1]) || line[index - 1] == '(')
            )
            {
                quoteEnd = current == '“' ? '”' : current;
                continue;
            }
            if (current == '(')
            {
                parentheses++;
                continue;
            }
            if (current == ')')
            {
                if (parentheses == 0)
                {
                    return -1;
                }
                parentheses--;
                continue;
            }

            if (
                parentheses == 0
                && (current is '-' or '–' or '—')
                && (
                    index > 0 && char.IsWhiteSpace(line[index - 1])
                    || index + 1 < line.Length && char.IsWhiteSpace(line[index + 1])
                )
            )
            {
                return index;
            }
        }

        return -1;
    }

    private static InstrumentationSupport ClassifyInstrumentation(string suffix)
    {
        var items = InstrumentItems.Split(suffix);
        if (
            items.Length > MaximumInstrumentItems
            || items.Any(item => item.Length == 0 || item.Length > MaximumInstrumentItemLength)
        )
        {
            return InstrumentationSupport.Unsupported;
        }

        var itemsWithoutInstrument = items.Where(item => !SupportedInstrumentItem(item)).ToArray();
        if (itemsWithoutInstrument.Length == items.Length)
        {
            return InstrumentationSupport.Unsupported;
        }

        // Lead/backing may be separate comma groups, but cannot anchor a roster alone.
        var unknownItems = itemsWithoutInstrument.Where(item => !ModifierItem(item)).ToArray();
        if (unknownItems.Length == 0)
        {
            return InstrumentationSupport.Supported;
        }

        // Continuation needs an explicit coordinated list and a complete known item.
        // A musical word inside prose, or an unknown standalone item, cannot anchor it.
        return items.Length > 1 && unknownItems.All(ShortListItem.IsMatch)
            ? InstrumentationSupport.Partial
            : InstrumentationSupport.Unsupported;
    }

    private static bool ModifierItem(string item) =>
        Regex
            .Matches(item, @"[\p{L}]+", RegexOptions.CultureInvariant, Timeout)
            .All(match => SuffixModifiers.Contains(match.Value));

    private static bool SupportedInstrumentItem(string item)
    {
        if (PedalSteel.IsMatch(item))
        {
            return true;
        }

        var words = Regex
            .Matches(item, @"[\p{L}]+", RegexOptions.CultureInvariant, Timeout)
            .Select(match => match.Value)
            .ToArray();
        return words.Any(InstrumentWords.Contains)
            && words.All(word => InstrumentWords.Contains(word) || SuffixModifiers.Contains(word));
    }

    private enum InstrumentationSupport
    {
        Unsupported,
        Partial,
        Supported,
    }

    private static bool UsableName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 100
        && !name.Contains(':')
        && !name.Contains('@');

    private static Guid AssertionUuid(string fieldHash, Guid referenceUuid) =>
        new(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}:{fieldHash}:{referenceUuid:D}"))[
                ..16
            ]
        );

    private static bool SupportsField(string field) =>
        field.ToLowerInvariant()
            is "taper"
                or "tapers"
                or "transferrer"
                or "transferrers"
                or "description"
                or "source"
                or "lineage"
                or "taper_notes"
                or "tag_notes"
                or "notes";

    private sealed record RosterContext(
        NormalizationTextEvidence Heading,
        List<NormalizationTextEvidence> Qualifiers
    );
}
