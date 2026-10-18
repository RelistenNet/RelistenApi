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
        "source-actor-aliases-v1/" + RecordingActorEvidenceExtractor.Version;

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
    private static readonly Regex Dash = new(
        @"(?<=\s)[-–—]\s*|[-–—](?=\s)",
        RegexOptions.CultureInvariant,
        Timeout
    );
    private static readonly Regex InstrumentSuffix = new(
        @"^(?:[\p{L}]+|\s|[,/&])+$",
        RegexOptions.CultureInvariant,
        Timeout
    );
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
                    && TryActorPhrase(line.Text, out var actorText)
                )
                {
                    if (markerCount > 0)
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

    private static bool TryActorPhrase(string line, out string actorText)
    {
        foreach (Match delimiter in Dash.Matches(line))
        {
            var actors = line[..delimiter.Index].Trim();
            var suffix = line[(delimiter.Index + delimiter.Length)..].Trim();
            if (actors.Length == 0 || !InstrumentSuffix.IsMatch(suffix))
            {
                continue;
            }
            var words = Regex
                .Matches(suffix, @"[\p{L}]+", RegexOptions.CultureInvariant, Timeout)
                .Select(match => match.Value)
                .ToArray();
            if (
                words.Any(InstrumentWords.Contains)
                && words.All(word =>
                    InstrumentWords.Contains(word) || SuffixModifiers.Contains(word)
                )
            )
            {
                actorText = actors;
                return true;
            }
        }

        actorText = "";
        return false;
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
