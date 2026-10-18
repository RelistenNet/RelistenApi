using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Extracts role-bearing mentions from recording fields. A mention preserves what the metadata
/// claims; identity resolution and acceptance are separate operations.
/// </summary>
public sealed class RecordingCreditExtractor
{
    /// <summary>Persist with extraction output so cached results can be invalidated when grammar changes.</summary>
    public const string Version = "recording-credits-v5/" + RecordingActorEvidenceExtractor.Version;

    private static readonly Regex ExplicitCredit = RecordingBylineGrammar.Credits;
    private static readonly Regex OperationActor = new(
        @"\b(?:by|via)\s+(?<name>[\p{L}\p{N}_.’' -]+?)(?=\s+\d{1,4}/|,|\(|;|\.\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private const string PersonalSuffix = @"(?:Jr|Sr|II|III|IV|V)\.?";
    internal static readonly Regex NameSeparator = new(
        @"\s*(?:,(?!\s*"
            + PersonalSuffix
            + @"(?=\s*(?:$|[,;&/(<]|\band\b)))|;|\s+and\s+|&|\s+/\s+)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex PersonalSuffixAtEnd = new(
        @"\b" + PersonalSuffix + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex Equipment = new(
        @"(?:\b(?:sbd|soundboard|audience|dat|flac|wav|akg|sony|schoeps|neumann|nakamichi|tascam|edirol|audacity|microphones?|unknown|anonymous|unspecified)\b|^[\p{L}]+-?\d[\p{L}\p{N}_.-]*$|[>→]|\b\d+\s*(?:bit|khz)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex Unknown = new(
        @"^(?:unknown|anonymous|none|n/?a|not\s+(?:known|specified|available)|unavailable|\?+|-)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    public RecordingCreditExtraction Extract(IReadOnlyDictionary<string, string?> fields)
    {
        var credits = new List<CreditMention>();
        var unresolved = new List<RecordingExtractionUnresolved>();

        foreach (var (field, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var structuredRole = FieldRole(field);
            foreach (var line in RecordingText.Lines(field, value))
            {
                // Bare structured names can contain punctuation; only role prose needs sentence splitting.
                IEnumerable<(string Text, int Start)> clauses =
                    structuredRole is not null && !ExplicitCredit.IsMatch(line.Text)
                        ? [(line.Text, 0)]
                        : CreditClauses(line.Text);
                foreach (var clause in clauses)
                {
                    var creditLine = new RecordingText.Line(
                        clause.Text,
                        line.Evidence,
                        clause.Start
                    );
                    var explicitCredits = ExplicitCredit.Matches(clause.Text);
                    ExtractNamedRoles(
                        creditLine,
                        explicitCredits,
                        structuredRole,
                        credits,
                        unresolved
                    );
                    ExtractOperationActors(creditLine, explicitCredits, credits, unresolved);
                }
            }
        }

        return new RecordingCreditExtraction(credits, unresolved);
    }

    private static IEnumerable<(string Text, int Start)> CreditClauses(string text)
    {
        var start = 0;
        foreach (Match boundary in Regex.Matches(text, @"\.\s+"))
        {
            var word = Regex.Match(text[..boundary.Index], @"[\p{L}]+$").Value;
            if (
                (word.Length == 1 && char.IsUpper(word[0]))
                || Regex.IsMatch(word, @"^(?:Dr|Mr|Mrs|Ms|Prof|Jr|Sr|St)$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text[..boundary.Index], @"\ba\.k\.a$", RegexOptions.IgnoreCase)
            )
            {
                // Initials and common name abbreviations do not end a credit sentence.
                continue;
            }

            yield return (text[start..boundary.Index], start);
            start = boundary.Index + boundary.Length;
        }

        yield return (text[start..], start);
    }

    private static void ExtractNamedRoles(
        RecordingText.Line line,
        MatchCollection explicitMatches,
        RecordingCreditRole? structuredRole,
        List<CreditMention> credits,
        List<RecordingExtractionUnresolved> unresolved
    )
    {
        if (explicitMatches.Count > 0)
        {
            foreach (Match match in explicitMatches)
            {
                if (
                    Regex.IsMatch(
                        line.Text[..match.Index],
                        @"\b(?:not|never)\s*$",
                        RegexOptions.IgnoreCase
                    )
                )
                {
                    continue;
                }

                var roles = RecordingBylineGrammar.Roles(match.Groups["role"].Value).ToArray();
                foreach (var role in roles)
                {
                    AddNames(
                        match.Groups["name"].Value,
                        role,
                        line.Evidence,
                        credits,
                        unresolved,
                        match.Groups["role"].Value,
                        roles,
                        line.VisibleStart + match.Groups["name"].Index
                    );
                }
            }
        }
        else if (structuredRole is not null)
        {
            // Some providers include an operation label and transfer date in the credit field.
            var text = Regex.Replace(
                line.Text,
                @"^EAC\s+transfer\s*:\s*",
                "",
                RegexOptions.IgnoreCase
            );
            AddNames(
                text,
                structuredRole.Value,
                line.Evidence,
                credits,
                unresolved,
                visibleStart: line.VisibleStart + line.Text.IndexOf(text, StringComparison.Ordinal)
            );
        }
    }

    private static void ExtractOperationActors(
        RecordingText.Line line,
        MatchCollection explicitCredits,
        List<CreditMention> credits,
        List<RecordingExtractionUnresolved> unresolved
    )
    {
        foreach (Match actor in OperationActor.Matches(line.Text))
        {
            // An explicit role clause owns its actor, even when a processing chain precedes it.
            // Let named-role extraction handle that claim, including negation and multiple roles.
            if (
                explicitCredits.Any(credit =>
                    actor.Index >= credit.Index && actor.Index < credit.Groups["name"].Index
                )
            )
            {
                continue;
            }

            var prefix = line.Text[..actor.Index];
            var boundary = Math.Max(
                prefix.LastIndexOf(", ", StringComparison.Ordinal),
                prefix.LastIndexOf(". ", StringComparison.Ordinal)
            );
            var operation = prefix[(boundary < 0 ? 0 : boundary + 2)..];
            if (
                operation.Contains('>')
                || Regex.IsMatch(operation, @"\b(?:DAE|EAC|encoding)\b", RegexOptions.IgnoreCase)
            )
            {
                AddNames(
                    actor.Groups["name"].Value,
                    RecordingCreditRole.Transferrer,
                    line.Evidence,
                    credits,
                    unresolved,
                    operation.Trim(),
                    visibleStart: line.VisibleStart + actor.Groups["name"].Index
                );
            }
        }
    }

    private static RecordingCreditRole? FieldRole(string field) =>
        field.ToLowerInvariant() switch
        {
            "taper" or "tapers" => RecordingCreditRole.Taper,
            "transferrer" or "transferrers" => RecordingCreditRole.Transferrer,
            "uploader" => RecordingCreditRole.Uploader,
            "collector" => RecordingCreditRole.Collector,
            _ => null,
        };

    private static void AddNames(
        string text,
        RecordingCreditRole role,
        NormalizationTextEvidence evidence,
        List<CreditMention> credits,
        List<RecordingExtractionUnresolved> unresolved,
        string? originalRole = null,
        IReadOnlyList<RecordingCreditRole>? jointRoles = null,
        int visibleStart = 0
    )
    {
        text = RecordingBylineGrammar.TrimActorList(text);
        var withoutDate = Regex.Replace(text, @"\s*\[\d{1,4}[/.-]\d{1,2}[/.-]\d{1,4}\]\s*$", "");
        var actors = new RecordingActorEvidenceExtractor().Enrich(
            withoutDate,
            evidence,
            jointRoles ?? [role],
            visibleStart
        );
        foreach (var actor in actors)
        {
            var name = actor.Name.Trim();
            // Prose sentence punctuation is not a name, but a credited suffix keeps its literal dot.
            if (originalRole is not null && !PersonalSuffixAtEnd.IsMatch(name))
            {
                name = name.TrimEnd('.');
            }

            if (name.Length == 0)
            {
                continue;
            }

            if (Unknown.IsMatch(name))
            {
                unresolved.Add(new("unknown_credit", evidence));
            }
            else if (
                Equipment.IsMatch(name)
                || name.Length > 100
                || name.Contains(':')
                || name.Contains('@')
                || RecordingActorEvidenceExtractor.IsContactOnly(name)
                || RecordingBylineGrammar.IsNonActorFragment(name)
            )
            {
                unresolved.Add(new("unparsed_credit", evidence));
            }
            else if (
                !credits.Any(credit =>
                    credit.Name == name
                    && credit.Role == role
                    && credit.Evidence == evidence
                    && credit.ActorFacts?.ReferenceUuid == actor.ActorFacts.ReferenceUuid
                )
            )
            {
                credits.Add(
                    new(
                        name,
                        role,
                        evidence,
                        role == RecordingCreditRole.Other ? originalRole : null,
                        actor.ActorFacts
                    )
                );
            }
        }
    }
}

/// <summary>
/// Reads text without changing its evidence coordinates. A decoded line cites its enclosing raw
/// substring so HTML entities and inline tags never produce fictional source offsets.
/// </summary>
internal static class RecordingText
{
    private static readonly Regex Boundaries = new(
        @"(?:</?(?:div|p|li|br|tr|h[1-6])\b[^>]*>|\r\n|[\r\n])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex Tags = new(
        @"<[^>]*>",
        RegexOptions.None,
        TimeSpan.FromSeconds(1)
    );

    internal sealed record Line(
        string Text,
        NormalizationTextEvidence Evidence,
        int VisibleStart = 0
    );

    internal static IEnumerable<Line> Lines(string field, string raw)
    {
        var start = 0;
        var startCodePoint = 0;
        foreach (Match boundary in Boundaries.Matches(raw))
        {
            // A local part such as "br" can resemble a block tag. Preserve the same complete
            // angle contact here as in inline markup stripping and raw-coordinate mapping.
            if (RecordingActorEvidenceExtractor.IsAngleContact(boundary.Value))
            {
                continue;
            }
            var endCodePoint =
                startCodePoint + CountRunes(raw.AsSpan(start, boundary.Index - start));
            var line = Read(field, raw, start, boundary.Index, startCodePoint, endCodePoint);
            if (line is not null)
            {
                yield return line;
            }

            // Boundaries begin and end with ASCII, so these disjoint spans never split a
            // surrogate pair. Counting each span also preserves replacement-rune offsets
            // for invalid surrogates without rescanning the growing prefix for every line.
            startCodePoint = endCodePoint + CountRunes(raw.AsSpan(boundary.Index, boundary.Length));
            start = boundary.Index + boundary.Length;
        }

        var last = Read(
            field,
            raw,
            start,
            raw.Length,
            startCodePoint,
            startCodePoint + CountRunes(raw.AsSpan(start))
        );
        if (last is not null)
        {
            yield return last;
        }
    }

    internal static string WithoutMarkup(string raw) =>
        Tags.Replace(
            raw,
            match => RecordingActorEvidenceExtractor.IsAngleContact(match.Value) ? match.Value : ""
        );

    private static int CountRunes(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    private static Line? Read(
        string field,
        string raw,
        int start,
        int end,
        int startCodePoint,
        int endCodePoint
    )
    {
        var quote = raw[start..end];
        var text = WebUtility.HtmlDecode(WithoutMarkup(quote)).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var pointer = "/" + field.Replace("~", "~0").Replace("/", "~1");
        return new(text, new(field, pointer, quote, startCodePoint, endCodePoint));
    }
}
