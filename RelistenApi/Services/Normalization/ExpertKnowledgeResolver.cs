using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Shared expert correspondence policy. Exact account corrections override broader aliases;
/// aliases never merge identities or infer credit roles. Readers consume these evaluated results.
/// </summary>
public sealed class ExpertKnowledgeResolver
{
    public const string Version = "expert-knowledge-v1";
    public const int MaximumRules = 10000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ExpertKnowledgeSnapshot snapshot;
    private readonly Dictionary<
        (string Domain, string Namespace, string Key),
        ExpertKnowledgeRule[]
    > accounts;
    private readonly Dictionary<
        (string Domain, string Namespace, string Literal),
        ExpertKnowledgeRule[]
    > aliases;

    public ExpertKnowledgeResolver(ExpertKnowledgeSnapshot snapshot)
    {
        this.snapshot = CreateSnapshot(snapshot.Rules);
        if (snapshot.Version != Version || snapshot.Sha256 != this.snapshot.Sha256)
        {
            throw new ArgumentException(
                "Expert knowledge snapshot differs from its policy or content pin."
            );
        }
        var active = this.snapshot.Rules.Where(rule => rule.State != "retracted").ToArray();
        accounts = active
            .Where(rule => rule.Definition.Selector.AccountStableKey != null)
            .GroupBy(rule =>
                (
                    rule.Definition.Domain,
                    rule.Definition.Selector.Namespace,
                    rule.Definition.Selector.AccountStableKey!
                )
            )
            .ToDictionary(group => group.Key, group => group.ToArray());
        aliases = active
            .Where(rule => rule.Definition.Selector.AccountStableKey == null)
            .GroupBy(rule =>
                (
                    rule.Definition.Domain,
                    rule.Definition.Selector.Namespace,
                    rule.Definition.Selector.Literal!
                )
            )
            .ToDictionary(group => group.Key, group => group.ToArray());
    }

    public ExpertKnowledgeEvaluation Evaluate(ExpertKnowledgeObservation observation)
    {
        ValidateDomain(observation.Domain);
        RequireKey(observation.Namespace, "observation namespace");
        RequireKey(observation.AccountStableKey, "observation account");
        var exact = accounts
            .GetValueOrDefault(
                (observation.Domain, observation.Namespace, observation.AccountStableKey),
                []
            )
            .Where(rule => Matches(rule.Definition.Selector, observation))
            .ToArray();
        ExpertKnowledgeRule[] broad =
            observation.Literal == null
                ? []
                : aliases
                    .GetValueOrDefault(
                        (observation.Domain, observation.Namespace, observation.Literal),
                        []
                    )
                    .Where(rule => Matches(rule.Definition.Selector, observation))
                    .ToArray();
        // A positive exact account correction is an intentional exception to reusable aliases.
        // Exact negative rules merely exclude their named targets and leave other choices usable.
        var effective = exact.Any(rule => rule.State == "accepted")
            ? exact
            : exact.Concat(broad).ToArray();
        var excluded = OrderedTargets(
                effective
                    .Where(rule => rule.State == "rejected")
                    .Select(rule => rule.Definition.Target)
            )
            .DistinctBy(TargetIdentity)
            .ToArray();
        var excludedKeys = excluded.Select(TargetIdentity).ToHashSet();
        var positive = OrderedTargets(
                effective
                    .Where(rule => rule.State == "accepted")
                    .Select(rule => rule.Definition.Target)
            )
            .Where(target => !excludedKeys.Contains(TargetIdentity(target)))
            .GroupBy(TargetIdentity)
            .SelectMany(group =>
            {
                // An attachment does not change the frozen reference's identity. Different
                // canonical or imported-song attachments remain a visible conflict.
                var canonical = group
                    .Where(target =>
                        target.CanonicalUuid != null || target.ImportedSongUuid != null
                    )
                    .ToArray();
                return canonical.Length > 0 ? canonical : [group.First()];
            })
            .ToArray();
        var outcome =
            positive.Length > 1 ? "conflict"
            : positive.Length == 1 ? "reviewed_correspondence"
            : excluded.Length > 0 ? "excluded_targets"
            : "no_match";
        return new(
            outcome,
            snapshot.Sha256,
            positive.Length == 1 ? positive[0] : null,
            excluded,
            effective
                .OrderBy(rule => rule.RuleUuid)
                .Select(rule => new ExpertKnowledgeAppliedRule(
                    rule.RuleUuid,
                    rule.Revision,
                    rule.State
                ))
                .ToArray(),
            positive.Length > 1 ? positive : []
        );
    }

    public static ExpertKnowledgeSnapshot CreateSnapshot(IReadOnlyList<ExpertKnowledgeRule> rules)
    {
        if (
            rules.Count > MaximumRules
            || rules.Select(rule => rule.RuleUuid).Distinct().Count() != rules.Count
        )
        {
            throw new ArgumentException(
                "Expert knowledge requires a complete bounded set of distinct rules."
            );
        }
        // Freeze nested caller-owned lists as well as the outer collection before indexing/hash.
        var frozen = JsonSerializer.Deserialize<ExpertKnowledgeRule[]>(
            JsonSerializer.SerializeToUtf8Bytes(rules, Json),
            Json
        )!;
        foreach (var rule in frozen)
        {
            if (
                rule.RuleUuid == Guid.Empty
                || rule.LastOperationUuid == Guid.Empty
                || rule.Revision < 1
                || rule.State is not ("accepted" or "rejected" or "retracted")
            )
            {
                throw new ArgumentException("Expert knowledge contains an invalid reviewed rule.");
            }
            ValidateDefinition(rule.Definition);
        }
        frozen = frozen.OrderBy(rule => rule.RuleUuid).ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = Version,
                rules = frozen.Select(rule => new
                {
                    rule.RuleUuid,
                    rule.Revision,
                    rule.State,
                    rule.Definition,
                    rule.LastOperationUuid,
                }),
            },
            Json
        );
        return new(
            Version,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            Array.AsReadOnly(frozen)
        );
    }

    public static void ValidateDefinition(ExpertKnowledgeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Selector);
        ArgumentNullException.ThrowIfNull(definition.Target);
        ArgumentNullException.ThrowIfNull(definition.Evidence);
        ValidateDomain(definition.Domain);
        if (definition.Target.Domain != definition.Domain)
        {
            throw new ArgumentException(
                "A correspondence target must belong to the rule's typed domain."
            );
        }
        RequireKey(definition.Target.Namespace, "target namespace");
        RequireKey(definition.Target.StableKey, "target key");
        if (
            definition.Target.ImportedSongUuid == Guid.Empty
            || definition.Target.ImportedSongUuid.HasValue && definition.Domain != "songs"
        )
        {
            throw new ArgumentException(
                "An imported song attachment requires a song target and a nonempty UUID."
            );
        }
        if (definition.Target.CanonicalUuid.HasValue && definition.Target.ImportedSongUuid.HasValue)
        {
            throw new ArgumentException(
                "A reviewed target can carry one canonical or imported-song attachment, not both."
            );
        }
        RequireKey(definition.Selector.Namespace, "selector namespace");
        var selector = definition.Selector;
        if (selector.AccountStableKey != null)
        {
            RequireKey(selector.AccountStableKey, "selector account");
        }
        if (
            selector.Literal != null
            && (
                selector.Literal.Length is 0 or > 4096
                || string.IsNullOrWhiteSpace(selector.Literal)
            )
        )
        {
            throw new ArgumentException("An alias requires a nonempty bounded literal.");
        }
        if (
            selector.AccountStableKey == null
            && (
                selector.Literal == null
                || selector.ArtistUuid == null
                    && selector.Provider == null
                    && selector.Locality == null
                    && selector.From == null
                    && selector.Through == null
            )
        )
        {
            throw new ArgumentException(
                "A reusable literal alias requires explicit artist, provider, locality or date context."
            );
        }
        if (
            (definition.Domain == "songs" || selector.Namespace == "repertoire")
            && selector.ArtistUuid == null
        )
        {
            throw new ArgumentException("Repertoire rules require their artist scope.");
        }
        if (
            selector.ArtistUuid == Guid.Empty
            || definition.Target.CanonicalUuid == Guid.Empty
            || selector.From > selector.Through
        )
        {
            throw new ArgumentException(
                "Rule scope requires valid identities and an inclusive ordered date range."
            );
        }
        if (selector.Provider != null)
        {
            RequireKey(selector.Provider, "provider");
        }
        if (selector.Locality != null)
        {
            RequireKey(selector.Locality, "locality");
        }
        if (
            definition.Evidence.Count is < 1 or > 16
            || string.IsNullOrWhiteSpace(definition.EvidenceDescription)
            || definition.EvidenceDescription.Length > 16384
        )
        {
            throw new ArgumentException(
                "A reviewed rule requires bounded retained evidence and a description."
            );
        }
        foreach (var pin in definition.Evidence)
        {
            if (
                string.IsNullOrWhiteSpace(pin.Path)
                || pin.Bytes is < 1 or > 4 * 1024 * 1024
                || pin.Sha256 is not { Length: 64 }
                || !pin.Sha256.All(Uri.IsHexDigit)
            )
            {
                throw new ArgumentException(
                    "Expert evidence requires a path, small byte bound and SHA-256 pin."
                );
            }
        }
        if (definition.Evidence.Sum(pin => pin.Bytes) > 8 * 1024 * 1024)
        {
            throw new ArgumentException("A rule's retained evidence exceeds eight MiB.");
        }
    }

    public static void ValidateDomain(string domain)
    {
        if (domain is not ("venues" or "songs" or "compositions" or "people"))
        {
            throw new ArgumentException(
                "Expert correspondence requires a supported normalization domain."
            );
        }
    }

    private static bool Matches(
        ExpertKnowledgeSelector selector,
        ExpertKnowledgeObservation observation
    ) =>
        (selector.Literal == null || selector.Literal == observation.Literal)
        && (selector.ArtistUuid == null || selector.ArtistUuid == observation.ArtistUuid)
        && (selector.Provider == null || selector.Provider == observation.Provider)
        && (selector.Locality == null || selector.Locality == observation.Locality)
        && (selector.From == null || observation.Date >= selector.From)
        && (selector.Through == null || observation.Date <= selector.Through);

    private static (string Domain, string Namespace, string StableKey) TargetIdentity(
        ExpertKnowledgeTarget target
    ) => (target.Domain, target.Namespace, target.StableKey);

    private static ExpertKnowledgeTarget[] OrderedTargets(
        IEnumerable<ExpertKnowledgeTarget> targets
    ) =>
        targets
            .Distinct()
            .OrderBy(target => target.Domain, StringComparer.Ordinal)
            .ThenBy(target => target.Namespace, StringComparer.Ordinal)
            .ThenBy(target => target.StableKey, StringComparer.Ordinal)
            .ThenBy(target => target.CanonicalUuid)
            .ThenBy(target => target.ImportedSongUuid)
            .ToArray();

    private static void RequireKey(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value != value.Trim())
        {
            throw new ArgumentException("A bounded exact " + name + " is required.");
        }
    }
}
