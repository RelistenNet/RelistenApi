using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>A role parser's display wording and source-scoped actor evidence; neither is a global identity.</summary>
public sealed record RecordingActorCredit(
    string Name,
    CreditedActorFacts<NormalizationTextEvidence> ActorFacts
);

/// <summary>
/// Extracts explicit actor attachments after role recognition. Names, aliases and profile/contact
/// hints retain separate evidence. A contact or profile never establishes actor kind, account
/// ownership, or equality with another recording's actor. Unattached hints remain unresolved.
/// </summary>
public sealed class RecordingActorEvidenceExtractor
{
    // This version identifies the fact/ID format. The enclosing credit producer also pins the
    // role and attachment grammar when cached output is reproduced or accepted.
    public const string Version = "recording-actor-evidence-v1";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Alias = new(
        @"\b(?:aka|a\.k\.a\.?|also\s+known\s+as)\s*:?\s*(?<alias>.+)$",
        Options,
        Timeout
    );
    private static readonly Regex Contact = new(
        @"[\p{L}\p{N}._%+\-]+\s*(?:@|\[\s*at\s*\]|\(\s*at\s*\)|\s+at\s+)\s*[\p{L}\p{N}\-]+(?:\s*(?:\.|\[\s*dot\s*\]|\(\s*dot\s*\)|\s+dot\s+)\s*[\p{L}\p{N}\-]+)+",
        Options,
        Timeout
    );
    private static readonly Regex Url = new(@"https?://[^\s<>""']+", Options, Timeout);
    private static readonly Regex AnchorTag = new(
        @"<a\b[^>]*\bhref\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)')[^>]*>(?<label>.*?)</a\s*>",
        Options | RegexOptions.Singleline,
        Timeout
    );
    private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.None, Timeout);
    private static readonly Regex PersonDescription = new(
        @"^\s*(?:a\s+person|an?\s+individual(?:\s+taper)?|an?\s+individual\s+person)\s*$",
        Options,
        Timeout
    );
    private static readonly Regex GroupDescription = new(
        @"^\s*(?:an?\s+)?(?:taping\s+collective|recording\s+group|taping\s+group|group\s+of\s+tapers)\s*$",
        Options,
        Timeout
    );
    private static readonly Regex ProjectName = new(
        @"\b(?:crew|project|team|collective)\b",
        Options,
        Timeout
    );

    /// <summary>
    /// visibleStart is the role parser's offset in the decoded enclosing line. It disambiguates
    /// repeated names in separate clauses. Every emitted coordinate still cites original raw text;
    /// inability to locate a decoded phrase is retained, never repaired with guessed offsets.
    /// </summary>
    public IReadOnlyList<RecordingActorCredit> Enrich(
        string actorText,
        NormalizationTextEvidence enclosingEvidence,
        IReadOnlyList<RecordingCreditRole> roles,
        int visibleStart = 0
    )
    {
        var map = new VisibleTextMap(enclosingEvidence);
        var start = map.Text.IndexOf(
            actorText,
            Math.Min(visibleStart, map.Text.Length),
            StringComparison.Ordinal
        );
        var parts = SplitActors(actorText)
            .Select(part => Prepare(part.Text, part.Start, start, map, enclosingEvidence))
            .ToArray();
        var references = parts.Select(part => part.ReferenceUuid).ToArray();
        var hints = ReadHints(map);
        return parts.Select(part => Build(part, parts, references, hints, roles, map)).ToArray();
    }

    private static PreparedActor Prepare(
        string text,
        int partStart,
        int phraseStart,
        VisibleTextMap map,
        NormalizationTextEvidence enclosing
    )
    {
        var removals = new List<(int Start, int Length)>();
        var attachments = new List<(int Start, int Length)>();
        var aliases =
            new List<(
                string Name,
                int Start,
                int Length,
                int StatementStart,
                int StatementLength
            )>();
        var issues = new List<(string Reason, NormalizationTextEvidence Evidence)>();
        ActorKindFact<NormalizationTextEvidence>? kind = null;
        var kindConflict = false;
        var context = new List<ActorContextFact<NormalizationTextEvidence>>();
        NormalizationTextEvidence Evidence(int start, int length) =>
            phraseStart < 0 ? enclosing : map.Evidence(phraseStart + partStart + start, length);

        foreach (var group in AttachmentGroups(text))
        {
            var body = text.Substring(group.Start + 1, group.Length - 2);
            var alias = Alias.Match(body);
            if (alias.Success)
            {
                var value = alias.Groups["alias"];
                var aliasName = value.Value.Trim().Trim('"', '\'', '“', '”');
                aliases.Add(
                    (
                        aliasName,
                        group.Start + 1 + value.Index,
                        value.Length,
                        group.Start,
                        group.Length
                    )
                );
                removals.Add(group);
            }
            else if (Contact.IsMatch(body) || Url.IsMatch(body))
            {
                var remainder = Contact.Replace(Url.Replace(body, ""), "");
                if (
                    Regex.IsMatch(
                        remainder,
                        @"^\s*(?:(?:e-?mail|profile)\s*:)?[\s;,]*$",
                        Options,
                        Timeout
                    )
                )
                {
                    attachments.Add(group);
                }
                else
                {
                    issues.Add(("ambiguous_actor_attachment", Evidence(group.Start, group.Length)));
                }
                removals.Add(group);
            }
            else if (PersonDescription.IsMatch(body) || GroupDescription.IsMatch(body))
            {
                var evidence = Evidence(group.Start, group.Length);
                var person = PersonDescription.IsMatch(body);
                var assertedKind = person ? "person" : "group";
                if (kindConflict || kind != null && kind.Kind != assertedKind)
                {
                    if (kind != null)
                    {
                        issues.AddRange(
                            kind.Evidence.Select(prior => ("conflicting_actor_kind", prior))
                        );
                    }
                    issues.Add(("conflicting_actor_kind", evidence));
                    kind = null;
                    kindConflict = true;
                }
                else
                {
                    kind = new(assertedKind, [.. (kind?.Evidence ?? []), evidence]);
                }
                context.Add(
                    new(
                        Key("context", evidence),
                        person
                            ? ActorContextKind.ExplicitPersonDescription
                            : ActorContextKind.ExplicitGroupDescription,
                        [evidence]
                    )
                );
                removals.Add(group);
            }
            else if (Regex.IsMatch(body, @"\bmember\s+of\b", Options, Timeout))
            {
                var evidence = Evidence(group.Start, group.Length);
                context.Add(
                    new(
                        Key("membership", evidence),
                        ActorContextKind.GroupMemberStatement,
                        [evidence]
                    )
                );
            }
        }

        // An alias marker outside parentheses consumes its explicitly attributed suffix. Ordinary
        // nicknames in parentheses remain literal display wording; their meaning was not asserted.
        var directAlias = Alias.Match(Mask(text, removals));
        if (directAlias.Success)
        {
            var originalAlias = directAlias;
            var value = originalAlias.Groups["alias"];
            var end = removals
                .Where(span => span.Start >= value.Index)
                .Select(span => span.Start)
                .DefaultIfEmpty(text.Length)
                .Min();
            var wording = text[value.Index..end].Trim().Trim('"', '\'', '“', '”');
            var aliasStart = text.IndexOf(wording, value.Index, StringComparison.Ordinal);
            var quoted =
                value.Value.TrimStart().StartsWith('"')
                || value.Value.TrimStart().StartsWith('“')
                || value.Value.TrimStart().StartsWith('\'');
            if (!quoted && RecordingCreditExtractor.NameSeparator.IsMatch(wording))
            {
                issues.Add(
                    (
                        "ambiguous_alias_boundary",
                        Evidence(originalAlias.Index, text.Length - originalAlias.Index)
                    )
                );
            }
            else
            {
                aliases.Add(
                    (
                        wording,
                        aliasStart,
                        wording.Length,
                        originalAlias.Index,
                        end - originalAlias.Index
                    )
                );
            }
            removals.Add((originalAlias.Index, text.Length - originalAlias.Index));
        }
        foreach (Match url in Url.Matches(text))
        {
            if (!removals.Any(span => Contains(span, url.Index, url.Length)))
            {
                // A nearby URL does not assert that it profiles this actor. Strip it from the
                // display wording, while ReadHints retains it as unattributed evidence.
                removals.Add((url.Index, url.Length));
            }
        }
        // A labeled trailing contact is not necessarily owned by the preceding actor. Keep the
        // actor wording usable, then retain that hint as unresolved rather than assigning it.
        var contactLabel = Regex.Match(
            text,
            @"\s+\b(?:contact|e-?mail|profile)\s*:",
            Options,
            Timeout
        );
        if (contactLabel.Success)
        {
            removals.Add((contactLabel.Index, text.Length - contactLabel.Index));
        }
        var name = Remove(text, removals).Trim().TrimEnd(',', ';');
        var nameStart = name.Length == 0 ? 0 : text.IndexOf(name, StringComparison.Ordinal);
        // Removing a middle attachment can leave noncontiguous display wording. Cite the whole
        // actor segment in that case instead of fabricating a contiguous name location.
        var anchor = nameStart < 0 ? Evidence(0, text.Length) : Evidence(nameStart, name.Length);
        var coordinateFallback = phraseStart < 0 ? $"unmapped:{partStart}:{text}" : "";
        var reference = Key("reference", anchor, coordinateFallback);
        name = Regex.Replace(name, @"\s+", " ", RegexOptions.None, Timeout);
        return new(
            name,
            Key("actor", anchor, coordinateFallback),
            reference,
            anchor,
            (phraseStart < 0 ? [] : aliases)
                .Select(alias => new ActorAliasFact<NormalizationTextEvidence>(
                    Key("alias", Evidence(alias.Start, alias.Length)),
                    Key("alias-reference", Evidence(alias.Start, alias.Length)),
                    alias.Name,
                    [
                        Evidence(alias.Start, alias.Length),
                        Evidence(alias.StatementStart, alias.StatementLength),
                    ]
                ))
                .ToArray(),
            phraseStart < 0 ? [] : context,
            phraseStart < 0 ? null : kind,
            (phraseStart < 0 ? [] : attachments)
                .Select(span => Evidence(span.Start, span.Length))
                .ToArray(),
            issues
                .Select(issue => new ActorEvidenceUnresolved<NormalizationTextEvidence>(
                    issue.Reason,
                    [reference],
                    [issue.Evidence]
                ))
                .ToArray(),
            phraseStart < 0
        );
    }

    private static RecordingActorCredit Build(
        PreparedActor actor,
        IReadOnlyList<PreparedActor> actors,
        IReadOnlyList<Guid> references,
        IReadOnlyList<Hint> hints,
        IReadOnlyList<RecordingCreditRole> roles,
        VisibleTextMap map
    )
    {
        var profiles = new List<ActorProfileFact<NormalizationTextEvidence>>();
        var contacts = new List<ActorContactFact<NormalizationTextEvidence>>();
        var unresolved = actor.Issues.ToList();
        foreach (var hint in hints)
        {
            // Bounded contact attachments or an HTML link whose label is this exact actor provide
            // local attachment evidence. Nearby standalone URLs and contacts do not.
            bool Attached(PreparedActor candidate) =>
                !candidate.MappingUnavailable
                && (
                    candidate.Attachments.Any(span =>
                        hint.Evidence.StartCodePoint >= span.StartCodePoint
                        && hint.Evidence.EndCodePoint <= span.EndCodePoint
                    )
                    || hint.LinkedActorEvidence is { } linked
                        && candidate.Name == hint.LinkedActorName
                        && candidate.Anchor.StartCodePoint >= linked.StartCodePoint
                        && candidate.Anchor.EndCodePoint <= linked.EndCodePoint
                );
            if (!Attached(actor))
            {
                if (!actors.Any(Attached))
                {
                    unresolved.Add(
                        new(
                            hint.ContactKey == null
                                ? "unattributed_profile"
                                : "unattributed_contact",
                            references,
                            [hint.Evidence]
                        )
                    );
                }
                continue;
            }
            if (hint.ContactKey is { } contact)
            {
                contacts.Add(
                    new(
                        Key("contact", hint.Evidence),
                        hint.Evidence.Quote,
                        "email",
                        contact,
                        [hint.Evidence, actor.Anchor]
                    )
                );
            }
            else if (Profile(hint.DecodedLiteral, actor.Name) is { } profile)
            {
                profiles.Add(
                    new(
                        Key("profile", hint.Evidence),
                        hint.Evidence.Quote,
                        profile.Provider,
                        profile.Namespace,
                        profile.Key,
                        ProjectName.IsMatch(actor.Name)
                            ? ActorProfileRelationship.CreditedProjectProfile
                            : ActorProfileRelationship.AttributedProfile,
                        [hint.Evidence, hint.LinkedActorEvidence ?? actor.Anchor]
                    )
                );
            }
            else
            {
                unresolved.Add(
                    new("unknown_profile_namespace", [actor.ReferenceUuid], [hint.Evidence])
                );
            }
        }
        if (actor.MappingUnavailable)
        {
            unresolved.Add(
                new("actor_text_mapping_unavailable", [actor.ReferenceUuid], [actor.Anchor])
            );
        }
        var relationships = new List<ActorCoReferenceFact<NormalizationTextEvidence>>();
        foreach (var alias in actor.Aliases)
        {
            relationships.Add(
                new(
                    Key("explicit-alias", alias.Evidence[0]),
                    ActorReferenceRelationship.ExplicitAlias,
                    [actor.ReferenceUuid, alias.ReferenceUuid],
                    [],
                    [actor.Anchor, .. alias.Evidence]
                )
            );
        }
        var distinctRoles = roles.Distinct().ToArray();
        if (distinctRoles.Length > 1)
        {
            relationships.Add(
                new(
                    Key("joint-roles", actor.Anchor),
                    ActorReferenceRelationship.JointRoles,
                    [actor.ReferenceUuid],
                    distinctRoles,
                    [map.EnclosingEvidence]
                )
            );
        }
        if (references.Count > 1)
        {
            relationships.Add(
                new(
                    Key("distinct-actors", map.EnclosingEvidence, string.Join(',', references)),
                    ActorReferenceRelationship.DistinctActors,
                    references,
                    distinctRoles,
                    [map.EnclosingEvidence]
                )
            );
        }
        var context = actor
            .Context.Append(
                new ActorContextFact<NormalizationTextEvidence>(
                    Key("named-role", actor.Anchor),
                    ActorContextKind.NamedRoleContext,
                    [map.EnclosingEvidence]
                )
            )
            .ToArray();
        var coverage = Enum.GetValues<ActorFactKind>()
            .Select(kind => new ActorEvidenceCoverage<NormalizationTextEvidence>(
                kind,
                false,
                "local_patterns_are_not_exhaustive",
                [map.EnclosingEvidence]
            ))
            .ToArray();
        return new(
            actor.Name,
            new(
                Version,
                actor.ActorUuid,
                actor.ReferenceUuid,
                actor.Anchor,
                actor.Aliases,
                profiles,
                contacts,
                context,
                actor.Kind,
                relationships,
                coverage,
                unresolved
            )
        );
    }

    private static IReadOnlyList<Hint> ReadHints(VisibleTextMap map)
    {
        var hints = new List<Hint>();
        var urls = Url.Matches(map.Text).Cast<Match>().ToArray();
        foreach (Match contact in Contact.Matches(map.Text))
        {
            // Query strings and paths can contain addresses unrelated to the credited actor.
            // Only a separate contact literal or explicit mailto link supplies contact evidence.
            if (
                urls.Any(url =>
                    contact.Index >= url.Index
                    && contact.Index + contact.Length <= url.Index + url.Length
                )
            )
            {
                continue;
            }
            if (
                Regex.IsMatch(
                    contact.Value,
                    @"^(?:download|published|available|listen|stream|hosted|recorded|uploaded|located)\s+at\b",
                    Options,
                    Timeout
                )
            )
            {
                continue;
            }
            hints.Add(
                new(
                    map.Evidence(contact.Index, contact.Length),
                    contact.Value,
                    ContactKey(contact.Value),
                    null,
                    null
                )
            );
        }
        foreach (var url in urls)
        {
            var value = url.Value.TrimEnd('.', ',', ')', ';');
            hints.Add(new(map.Evidence(url.Index, value.Length), value, null, null, null));
        }
        foreach (Match anchor in AnchorTag.Matches(map.EnclosingEvidence.Quote))
        {
            var href = anchor.Groups["href"];
            var decoded = WebUtility.HtmlDecode(href.Value);
            var label = WebUtility.HtmlDecode(Tag.Replace(anchor.Groups["label"].Value, "")).Trim();
            var evidence = map.RawEvidence(href.Index, href.Length);
            var attachment = map.RawEvidence(anchor.Index, anchor.Length);
            if (
                decoded.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                && Contact.Match(decoded[7..]) is { Success: true } contact
            )
            {
                hints.Add(new(evidence, decoded, ContactKey(contact.Value), label, attachment));
            }
            else if (
                Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
            )
            {
                hints.Add(new(evidence, decoded, null, label, attachment));
            }
        }
        return hints
            .DistinctBy(hint =>
                (hint.Evidence.StartCodePoint, hint.Evidence.EndCodePoint, hint.ContactKey)
            )
            .ToArray();
    }

    private static (string Provider, string Namespace, string Key)? Profile(
        string value,
        string actorName
    )
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        var path = uri.AbsolutePath.Trim('/');
        if (
            host == "archive.org"
            && path.StartsWith("details/@", StringComparison.Ordinal)
            && path[9..].Length > 0
            && !path[9..].Contains('/')
        )
        {
            return (host, "account", Uri.UnescapeDataString(path[9..]));
        }
        if (host == "taperssection.com")
        {
            var id = Regex.Match(uri.Query, @"(?:[?;&])u=(?<id>\d+)(?:[;&]|$)", Options, Timeout);
            if (
                id.Success
                && uri.Query.Contains("action=profile", StringComparison.OrdinalIgnoreCase)
            )
            {
                return (host, "forum_member", id.Groups["id"].Value);
            }
        }
        if (
            (host is "db.etree.org" or "github.com" or "soundcloud.com")
            && path.Length > 0
            && !path.Contains('/')
            && !path.Contains('.')
        )
        {
            return (host, "account", Uri.UnescapeDataString(path));
        }
        if (ProjectName.IsMatch(actorName) && path.Length == 0 && uri.Query.Length == 0)
        {
            return (host, "website", uri.GetLeftPart(UriPartial.Authority));
        }
        return null;
    }

    private static string ContactKey(string value)
    {
        var at = Regex.Replace(value, @"\[\s*at\s*\]|\(\s*at\s*\)|\s+at\s+", "@", Options, Timeout);
        var dots = Regex.Replace(
            at,
            @"\[\s*dot\s*\]|\(\s*dot\s*\)|\s+dot\s+",
            ".",
            Options,
            Timeout
        );
        return Regex.Replace(dots, @"\s+", "", RegexOptions.None, Timeout).ToLowerInvariant();
    }

    private static IEnumerable<(string Text, int Start)> SplitActors(string text)
    {
        var start = 0;
        var groups = AttachmentGroups(text).ToArray();
        var urls = Url.Matches(text).Cast<Match>().ToArray();
        var directAlias = Alias.Match(Mask(text, groups));
        foreach (Match separator in RecordingCreditExtractor.NameSeparator.Matches(text))
        {
            if (
                groups.Any(group => Contains(group, separator.Index, separator.Length))
                || urls.Any(url =>
                    separator.Index >= url.Index && separator.Index < url.Index + url.Length
                )
                || directAlias.Success
                    && separator.Index >= directAlias.Index
                    && !separator.Value.Contains(';')
            )
            {
                continue;
            }
            yield return (text[start..separator.Index], start);
            start = separator.Index + separator.Length;
        }
        yield return (text[start..], start);
    }

    private static IEnumerable<(int Start, int Length)> Parentheses(string text)
    {
        var start = -1;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                if (depth++ == 0)
                {
                    start = i;
                }
            }
            else if (text[i] == ')' && depth > 0 && --depth == 0)
            {
                yield return (start, i - start + 1);
            }
        }
    }

    // An email enclosed in angle brackets is source text, even in a field that also contains
    // HTML. Require the entire interior to be a contact so ordinary tags and attributes stay HTML.
    internal static bool IsAngleContact(string text)
    {
        if (text.Length < 3 || text[0] != '<' || text[^1] != '>')
        {
            return false;
        }
        var body = WebUtility.HtmlDecode(text[1..^1]);
        if (!body.Contains('@') || body.Any(char.IsWhiteSpace))
        {
            return false;
        }
        var contact = Contact.Match(body);
        return contact.Success && contact.Index == 0 && contact.Length == body.Length;
    }

    private static IEnumerable<(int Start, int Length)> AttachmentGroups(string text)
    {
        var parentheses = Parentheses(text).ToArray();
        foreach (var group in parentheses)
        {
            yield return group;
        }
        foreach (Match angle in Regex.Matches(text, @"<[^>]*>", RegexOptions.None, Timeout))
        {
            if (
                IsAngleContact(angle.Value)
                && !parentheses.Any(group => Contains(group, angle.Index, angle.Length))
            )
            {
                yield return (angle.Index, angle.Length);
            }
        }
    }

    private static bool Contains((int Start, int Length) span, int start, int length) =>
        start >= span.Start && start + length <= span.Start + span.Length;

    private static string Mask(string text, IEnumerable<(int Start, int Length)> spans)
    {
        var characters = text.ToCharArray();
        foreach (var span in spans)
        {
            Array.Fill(characters, ' ', span.Start, span.Length);
        }
        return new string(characters);
    }

    private static string Remove(string text, IEnumerable<(int Start, int Length)> spans)
    {
        var removed = new bool[text.Length];
        foreach (var span in spans)
        {
            Array.Fill(removed, true, span.Start, span.Length);
        }
        return string.Concat(text.Where((_, index) => !removed[index]));
    }

    private static Guid Key(string kind, NormalizationTextEvidence evidence, string suffix = "") =>
        new(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{Version}:{kind}:{evidence.Field}:{evidence.JsonPointer}:{evidence.StartCodePoint}:{evidence.EndCodePoint}:{suffix}"
                )
            )[..16]
        );

    private sealed record PreparedActor(
        string Name,
        Guid ActorUuid,
        Guid ReferenceUuid,
        NormalizationTextEvidence Anchor,
        IReadOnlyList<ActorAliasFact<NormalizationTextEvidence>> Aliases,
        IReadOnlyList<ActorContextFact<NormalizationTextEvidence>> Context,
        ActorKindFact<NormalizationTextEvidence>? Kind,
        IReadOnlyList<NormalizationTextEvidence> Attachments,
        IReadOnlyList<ActorEvidenceUnresolved<NormalizationTextEvidence>> Issues,
        bool MappingUnavailable
    );

    private sealed record Hint(
        NormalizationTextEvidence Evidence,
        string DecodedLiteral,
        string? ContactKey,
        string? LinkedActorName,
        NormalizationTextEvidence? LinkedActorEvidence
    );

    /// <summary>
    /// Maps decoded UTF-16 text positions back to enclosing raw UTF-16 intervals. Every decoded
    /// entity character shares that entity's complete raw interval. Citation offsets are converted
    /// to code points only at the edge; tags and surrogate pairs cannot shift them silently.
    /// </summary>
    private sealed class VisibleTextMap
    {
        public NormalizationTextEvidence EnclosingEvidence { get; }
        public string Text { get; }
        private readonly List<(int Start, int End)> positions = [];

        public VisibleTextMap(NormalizationTextEvidence enclosing)
        {
            EnclosingEvidence = enclosing;
            var raw = enclosing.Quote;
            var visible = new StringBuilder();
            for (var i = 0; i < raw.Length; )
            {
                if (
                    raw[i] == '<'
                    && raw.IndexOf('>', i) is var tagEnd
                    && tagEnd >= 0
                    && !IsAngleContact(raw[i..(tagEnd + 1)])
                )
                {
                    i = tagEnd + 1;
                    continue;
                }
                var end = i + 1;
                var value = raw[i].ToString();
                if (
                    raw[i] == '&'
                    && raw.IndexOf(';', i) is var entityEnd
                    && entityEnd > i
                    && entityEnd - i <= 32
                )
                {
                    var entity = raw[i..(entityEnd + 1)];
                    var decoded = WebUtility.HtmlDecode(entity);
                    if (decoded != entity)
                    {
                        value = decoded;
                        end = entityEnd + 1;
                    }
                }
                visible.Append(value);
                positions.AddRange(value.Select(_ => (i, end)));
                i = end;
            }
            var untrimmed = visible.ToString();
            var leading = untrimmed.Length - untrimmed.TrimStart().Length;
            Text = untrimmed.Trim();
            positions = positions.Skip(leading).Take(Text.Length).ToList();
        }

        public NormalizationTextEvidence Evidence(int start, int length) =>
            length == 0
                ? EnclosingEvidence
                : RawEvidence(
                    positions[start].Start,
                    positions[start + length - 1].End - positions[start].Start
                );

        public NormalizationTextEvidence RawEvidence(int start, int length)
        {
            var raw = EnclosingEvidence.Quote;
            return new(
                EnclosingEvidence.Field,
                EnclosingEvidence.JsonPointer,
                raw.Substring(start, length),
                EnclosingEvidence.StartCodePoint + raw[..start].EnumerateRunes().Count(),
                EnclosingEvidence.StartCodePoint + raw[..(start + length)].EnumerateRunes().Count()
            );
        }
    }
}
