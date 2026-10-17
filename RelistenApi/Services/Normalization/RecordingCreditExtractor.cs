using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    public const string Version = "recording-credits-v1";

    private const string CreditVerbs =
        @"(?:recorded|taped|transferred|mastered|uploaded|collected|matrixed)";
    private static readonly Regex ExplicitCredit = new(
        @"\b(?<role>(?:renamed\s+and\s+tagged|(?:[\p{L}]+ly\s+)?"
            + CreditVerbs
            + @"(?:\s+and\s+(?:[\p{L}]+ly\s+)?"
            + CreditVerbs
            + @")*)\s+by\b|(?:tapers?|transferrers?|recording\s+engineer|mastering\s+engineer|matrix\s+builder|uploader|collector|musicians?)\b(?=\s*:)|matrix\s+by\b)(?:\s*:\s*|\s+)(?<name>.+?)(?=;|$|\s+(?:(?:and\s+)?(?:[\p{L}]+ly\s+)?"
            + CreditVerbs
            + @"\s+by\b|taper\s*:|transferrer\s*:|(?:from|using|via|with)\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex OperationActor = new(
        @"\b(?:by|via)\s+(?<name>[\p{L}\p{N}_.’' -]+?)(?=\s+\d{1,4}/|,|\(|;|\.\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex NameSeparator = new(
        @"\s*(?:,|;|\s+and\s+|&|\s+/\s+)\s*",
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
                IEnumerable<string> clauses =
                    structuredRole is not null && !ExplicitCredit.IsMatch(line.Text)
                        ? [line.Text]
                        : CreditClauses(line.Text);
                foreach (var clause in clauses)
                {
                    var creditLine = new RecordingText.Line(clause, line.Evidence);
                    ExtractNamedRoles(creditLine, structuredRole, credits, unresolved);
                    ExtractOperationActors(creditLine, credits, unresolved);
                }
            }
        }

        return new RecordingCreditExtraction(credits, unresolved);
    }

    private static IEnumerable<string> CreditClauses(string text)
    {
        var start = 0;
        foreach (Match boundary in Regex.Matches(text, @"\.\s+"))
        {
            var word = Regex.Match(text[..boundary.Index], @"[\p{L}]+$").Value;
            if (
                (word.Length == 1 && char.IsUpper(word[0]))
                || Regex.IsMatch(word, @"^(?:Dr|Mr|Mrs|Ms|Prof|Jr|Sr|St)$", RegexOptions.IgnoreCase)
            )
            {
                // Initials and common name abbreviations do not end a credit sentence.
                continue;
            }

            yield return text[start..boundary.Index];
            start = boundary.Index + boundary.Length;
        }

        yield return text[start..];
    }

    private static void ExtractNamedRoles(
        RecordingText.Line line,
        RecordingCreditRole? structuredRole,
        List<CreditMention> credits,
        List<RecordingExtractionUnresolved> unresolved
    )
    {
        var explicitMatches = ExplicitCredit.Matches(line.Text);
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

                foreach (var role in Roles(match.Groups["role"].Value))
                {
                    AddNames(
                        match.Groups["name"].Value,
                        role,
                        line.Evidence,
                        credits,
                        unresolved,
                        match.Groups["role"].Value
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
            AddNames(text, structuredRole.Value, line.Evidence, credits, unresolved);
        }
    }

    private static void ExtractOperationActors(
        RecordingText.Line line,
        List<CreditMention> credits,
        List<RecordingExtractionUnresolved> unresolved
    )
    {
        foreach (Match actor in OperationActor.Matches(line.Text))
        {
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
                    operation.Trim()
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

    private static IEnumerable<RecordingCreditRole> Roles(string wording)
    {
        var role = wording.ToLowerInvariant();
        if (role.Contains("engineer"))
        {
            yield return role.StartsWith("mastering")
                ? RecordingCreditRole.MasteringEngineer
                : RecordingCreditRole.RecordingEngineer;
            yield break;
        }

        if (role.Contains("recorded") || role.Contains("tape"))
        {
            yield return RecordingCreditRole.Taper;
        }
        if (role.Contains("transfer"))
        {
            yield return RecordingCreditRole.Transferrer;
        }
        if (role.Contains("mastered"))
        {
            yield return RecordingCreditRole.MasteringEngineer;
        }
        if (role.Contains("matrix"))
        {
            yield return RecordingCreditRole.MatrixBuilder;
        }
        if (role.Contains("upload"))
        {
            yield return RecordingCreditRole.Uploader;
        }
        if (role.Contains("collect"))
        {
            yield return RecordingCreditRole.Collector;
        }
        if (role.Contains("renamed") || role.Contains("tagged"))
        {
            yield return RecordingCreditRole.Other;
        }
        if (role.Contains("musician"))
        {
            yield return RecordingCreditRole.Musician;
        }
    }

    private static void AddNames(
        string text,
        RecordingCreditRole role,
        NormalizationTextEvidence evidence,
        List<CreditMention> credits,
        List<RecordingExtractionUnresolved> unresolved,
        string? originalRole = null
    )
    {
        var withoutDate = Regex.Replace(text, @"\s*\[\d{1,4}[/.-]\d{1,2}[/.-]\d{1,4}\]\s*$", "");
        // Contact addresses qualify the credit's evidence, not the person's display name.
        var withoutContact = Regex.Replace(withoutDate, @"\s*\([^)]*@[^)]*\)", "");
        foreach (var part in NameSeparator.Split(withoutContact))
        {
            var name = originalRole is null ? part.Trim() : part.Trim().TrimEnd('.');
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
            )
            {
                unresolved.Add(new("unparsed_credit", evidence));
            }
            else if (
                !credits.Any(credit =>
                    credit.Name == name && credit.Role == role && credit.Evidence == evidence
                )
            )
            {
                credits.Add(
                    new(
                        name,
                        role,
                        evidence,
                        role == RecordingCreditRole.Other ? originalRole : null
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

    internal sealed record Line(string Text, NormalizationTextEvidence Evidence);

    internal static IEnumerable<Line> Lines(string field, string raw)
    {
        var start = 0;
        foreach (Match boundary in Boundaries.Matches(raw))
        {
            var line = Read(field, raw, start, boundary.Index);
            if (line is not null)
            {
                yield return line;
            }

            start = boundary.Index + boundary.Length;
        }

        var last = Read(field, raw, start, raw.Length);
        if (last is not null)
        {
            yield return last;
        }
    }

    private static Line? Read(string field, string raw, int start, int end)
    {
        var quote = raw[start..end];
        var text = WebUtility.HtmlDecode(Tags.Replace(quote, "")).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var pointer = "/" + field.Replace("~", "~0").Replace("/", "~1");
        return new(
            text,
            new(
                field,
                pointer,
                quote,
                raw[..start].EnumerateRunes().Count(),
                raw[..end].EnumerateRunes().Count()
            )
        );
    }
}
