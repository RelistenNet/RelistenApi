using System.Text;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Joins an authenticated source mention to facts extracted from its complete original field.
/// It never uses another mention, a candidate pair or a label answer to attach context.
/// </summary>
public sealed class PeopleSourceContextProjection
{
    public const string Version = "people-source-context-v1";
    private readonly Dictionary<(string EvidenceId, string Field), SourceField> fields = [];

    public PeopleSourceContextProjection(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> sources,
        CancellationToken cancellationToken = default
    )
    {
        var extractor = new RecordingCreditExtractor();
        foreach (var (evidenceId, source) in sources)
        {
            foreach (var (field, raw) in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (raw == null)
                {
                    continue;
                }
                var extraction = extractor.Extract(
                    new Dictionary<string, string?> { [field] = raw }
                );
                fields.Add((evidenceId, field), new(raw, extraction.Credits));
            }
        }
    }

    public PeopleSourceContextResult Project(
        string evidenceId,
        string mentionName,
        string mentionKind,
        Guid artistUuid,
        NormalizationTextEvidence anchor
    )
    {
        var fallback = PeopleActorProjection.Project(
            new CreditMention(mentionName, RecordingCreditRole.Other, anchor),
            artistUuid
        );
        if (mentionKind != "name")
        {
            return new(
                fallback,
                "unsupported_mention_expression",
                null,
                null,
                ["unsupported_mention_expression"]
            );
        }
        if (!fields.TryGetValue((evidenceId, anchor.Field), out var field))
        {
            return new(fallback, "name_only", null, null, ["source_field_not_found"]);
        }
        if (!IsOriginalAnchor(field.Raw, anchor))
        {
            return new(fallback, "name_only", null, null, ["source_anchor_mismatch"]);
        }

        var matches = field
            .Credits.Where(credit => credit.ActorFacts != null)
            .Select(credit => (Credit: credit, Via: Match(credit, anchor, mentionName)))
            .Where(value => value.Via != null)
            // A single named actor may carry both taper and transferrer roles. These are the
            // same source-local reference; independent same-spelled references stay distinct.
            .GroupBy(value => value.Credit.ActorFacts!.ReferenceUuid)
            .Select(group => group.First())
            .ToArray();
        if (matches.Length != 1)
        {
            return new(
                fallback,
                "name_only",
                null,
                null,
                [matches.Length == 0 ? "source_actor_not_found" : "ambiguous_source_actor"]
            );
        }
        var match = matches[0];
        var facts = match.Credit.ActorFacts!;
        var projection = PeopleActorProjection.Project(match.Credit, artistUuid);
        var issues = facts
            .Unresolved.Select(issue => issue.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!projection.Complete)
        {
            issues.Add("context_query_incomplete");
        }
        return new(projection, "matched", match.Via, facts.ReferenceUuid, issues, facts);
    }

    private static string? Match(
        CreditMention credit,
        NormalizationTextEvidence anchor,
        string name
    )
    {
        var facts = credit.ActorFacts!;
        if (facts.Anchor == anchor && (name == credit.Name || name == anchor.Quote))
        {
            return "actor_anchor";
        }
        // The supported extractor puts the alias's exact source citation first, followed
        // by its statement. It may retain quote delimiters; an inner guessed subspan and
        // the enclosing statement cannot stand in for that original alias citation.
        return facts.Aliases.Any(alias =>
            alias.Evidence[0] == anchor
            && (name == alias.Wording || name == anchor.Quote)
            && facts.Relationships.Any(relationship =>
                relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
                && relationship.ReferenceUuids.Contains(facts.ReferenceUuid)
                && relationship.ReferenceUuids.Contains(alias.ReferenceUuid)
            )
        )
            ? "explicit_alias"
            : null;
    }

    private static bool IsOriginalAnchor(string raw, NormalizationTextEvidence anchor)
    {
        var runes = raw.EnumerateRunes().ToArray();
        var pointer = "/" + anchor.Field.Replace("~", "~0").Replace("/", "~1");
        return anchor.JsonPointer == pointer
            && anchor.StartCodePoint >= 0
            && anchor.EndCodePoint > anchor.StartCodePoint
            && anchor.EndCodePoint <= runes.Length
            && string.Concat(
                runes
                    .Skip(anchor.StartCodePoint)
                    .Take(anchor.EndCodePoint - anchor.StartCodePoint)
                    .Select(rune => rune.ToString())
            ) == anchor.Quote;
    }

    private sealed record SourceField(string Raw, IReadOnlyList<CreditMention> Credits);
}

public sealed record PeopleSourceContextResult(
    PeopleActorProjectionResult Projection,
    string Status,
    string? MatchedVia,
    Guid? ReferenceUuid,
    IReadOnlyList<string> Issues,
    CreditedActorFacts<NormalizationTextEvidence>? Facts = null
);
