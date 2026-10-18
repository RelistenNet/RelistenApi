using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class VenueResolutionPolicyEvaluator
{
    private static VenueCorrespondenceDecision EvaluatePhysicalIdentity(
        VenueCorrespondenceFacts facts,
        VenueAcceptancePolicyArtifact artifact
    )
    {
        var supported = facts.SupportedFacts;
        if (supported == null || !SupportedPoolMatchesRetainedFacts(facts, supported))
        {
            return Review(artifact.MatchingRuleId, "missing_or_inconsistent_supported_facts", []);
        }

        var decisions = facts
            .Candidates.Select(candidate => ComparePhysicalIdentity(facts, candidate, supported))
            .ToArray();
        var eligible = decisions.Where(candidate => candidate.Eligible).ToArray();
        return eligible.Length == 1
            ? new(
                VenueCorrespondenceAction.ProposeMatch,
                eligible[0].VenueUuid,
                artifact.MatchingRuleId,
                "unique_supported_physical_venue",
                decisions
            )
            : Review(
                artifact.MatchingRuleId,
                eligible.Length == 0 ? "no_supported_venue" : "competing_supported_venues",
                decisions
            );
    }

    private static VenueCorrespondenceCandidateDecision ComparePhysicalIdentity(
        VenueCorrespondenceFacts facts,
        VenueCandidateFacts candidate,
        VenueSupportedCorrespondenceFacts supported
    )
    {
        var source = supported.Source.Match;
        var target = supported.Candidates.Single(value =>
            value.Match.Uuid == candidate.Header.Uuid
        );
        // Qualification belongs to the retained processor projection. Recompute comparison here;
        // callers cannot make an arbitrary SupportedRules value into positive identity evidence.
        var comparison = new IdentityCandidateMatcher().CompareVenues(source, target.Match);
        var contradictions = comparison
            .Features.Concat(
                facts
                    .Comparisons.Single(value => value.CandidateUuid == candidate.Header.Uuid)
                    .Features
            )
            .Where(value => value.Agreement == IdentityFeatureAgreement.Conflicts)
            .Select(value => "matcher_conflict:" + value.Name)
            .ToList();
        CompareAuthorities(facts, candidate.Header.Uuid, contradictions);
        var parents = candidate
            .Parents.Select(value => value.ResolvedParentVenueUuid)
            .Distinct()
            .ToArray();
        if (parents.Length > 1)
        {
            contradictions.Add("alternative_parent_site_accounts");
        }
        if (source.ParentVenueUuid is { } parent && (parents.Length != 1 || parents[0] != parent))
        {
            contradictions.Add("known_parent_site_conflict");
        }

        var features = new List<string>();
        var qualifiedIdentity =
            comparison.SupportedRules.Contains(IdentityMatchRule.QualifiedEntityIdentifier)
            && source.ExternalIdentities.Any(first =>
                first.IdentifiesEntity
                && !StringComparer.OrdinalIgnoreCase.Equals(first.Provider, "legacy_catalog")
                && target.Match.ExternalIdentities.Any(second =>
                    second.IdentifiesEntity && SameIdentifier(first, second)
                )
            );
        if (qualifiedIdentity)
        {
            features.Add("qualified_physical_entity_identifier");
        }

        // A shared address can contain several rooms. Address evidence must identify the same
        // kind and, for a room, the same resolved parent; missing kind is never a default site.
        var supportedAddress =
            comparison.SupportedRules.Contains(IdentityMatchRule.VenueNameAndAddress)
            && source.Kind == target.Match.Kind
            && (
                source.Kind == "site"
                || source.Kind == "room"
                    && source.ParentVenueUuid != null
                    && source.ParentVenueUuid == target.Match.ParentVenueUuid
            )
            && source.LocationAssertions != null
            && target.Match.LocationAssertions != null;
        if (supportedAddress)
        {
            features.Add("exact_name_address_and_resolved_locality");
        }
        if (!qualifiedIdentity && !supportedAddress)
        {
            features.Add("insufficient_supported_correspondence");
        }

        return new(
            candidate.Header.Uuid,
            (qualifiedIdentity || supportedAddress) && contradictions.Count == 0,
            features,
            contradictions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            supported.Source.Evidence.Concat(target.Evidence).Distinct().ToArray()
        );
    }

    private static bool SupportedPoolMatchesRetainedFacts(
        VenueCorrespondenceFacts facts,
        VenueSupportedCorrespondenceFacts supported
    )
    {
        if (
            supported.Source.Evidence.Count == 0
            || supported.Candidates.Any(value => value.Evidence.Count == 0)
            || supported.Candidates.Count != facts.Candidates.Count
            || !supported
                .Candidates.Select(value => value.Match.Uuid)
                .Order()
                .SequenceEqual(facts.Candidates.Select(value => (Guid?)value.Header.Uuid).Order())
        )
        {
            return false;
        }

        var source = supported.Source.Match;
        if (
            source.Uuid != facts.Source.Uuid
            || source.Kind != facts.Source.Kind
            || source.ParentVenueUuid != facts.Source.ParentVenueUuid
            || source.ObservedDate != facts.Source.ObservedDate
            || !OrderedNames(source.Names).SequenceEqual(OrderedNames(facts.Source.Names))
            || !source
                .Names.Select(value => value.Wording)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    facts
                        .OriginalVenueObservation.Names.Select(value => value.Value)
                        .Order(StringComparer.Ordinal)
                )
            || !SameIdentifiers(source.ExternalIdentities, facts.Source.ExternalIdentities)
            || !SameIdentifiers(
                source.ExternalIdentities,
                facts
                    .OriginalVenueObservation.Identities.Select(
                        value => new MatchingExternalIdentity(
                            value.Identity.Provider,
                            value.Identity.ResourceType,
                            value.Identity.ProviderKey
                        )
                    )
                    .ToArray()
            )
            || SourceLocations(source)
                .Any(value =>
                    value.Location.Address != facts.OriginalVenueObservation.Address?.Value
                )
        )
        {
            return false;
        }

        return facts.Candidates.All(candidate =>
            MatchesRetainedCandidate(
                supported
                    .Candidates.Single(value => value.Match.Uuid == candidate.Header.Uuid)
                    .Match,
                candidate
            )
        );
    }

    private static bool MatchesRetainedCandidate(
        VenueMatchRecord match,
        VenueCandidateFacts candidate
    )
    {
        var parents = candidate
            .Parents.Select(value => value.ResolvedParentVenueUuid)
            .Distinct()
            .ToArray();
        var names = candidate.Names.Select(value => new MatchingVenueName(
            value.Wording,
            value.ValidFrom,
            value.ValidTo,
            value.Language
        ));
        var identifiers = candidate
            .ExternalIdentities.Select(value => new MatchingExternalIdentity(
                value.Provider,
                value.ResourceType,
                value.ProviderKey
            ))
            .ToArray();
        var locations = candidate.Locations.Select(value =>
            (value.Address, value.ValidFrom, value.ValidTo)
        );
        return match.Kind == (candidate.Header.Kind ?? "")
            && match.ParentVenueUuid == (parents.Length == 1 ? parents[0] : null)
            && match.ObservedDate == null
            && OrderedNames(match.Names).SequenceEqual(OrderedNames(names))
            && SameIdentifiers(match.ExternalIdentities, identifiers)
            && SourceLocations(match)
                .Select(LocationIdentity)
                .Order()
                .SequenceEqual(locations.Order());
    }

    private static IOrderedEnumerable<MatchingVenueName> OrderedNames(
        IEnumerable<MatchingVenueName> names
    ) =>
        names
            .OrderBy(value => value.Wording, StringComparer.Ordinal)
            .ThenBy(value => value.ValidFrom)
            .ThenBy(value => value.ValidTo)
            .ThenBy(value => value.Language, StringComparer.Ordinal);

    private static IEnumerable<MatchingVenueLocationAssertion> SourceLocations(
        VenueMatchRecord match
    ) =>
        match.LocationAssertions
        ?? (
            match.Location == null
                ? []
                : new[]
                {
                    new MatchingVenueLocationAssertion(
                        match.Location,
                        match.ObservedDate,
                        match.ObservedDate
                    ),
                }
        );

    private static (string? Address, DateOnly? From, DateOnly? To) LocationIdentity(
        MatchingVenueLocationAssertion value
    ) => (value.Location.Address, value.ValidFrom, value.ValidTo);

    private static bool SameIdentifiers(
        IReadOnlyList<MatchingExternalIdentity> first,
        IReadOnlyList<MatchingExternalIdentity> second
    ) => first.Select(IdentifierKey).Order().SequenceEqual(second.Select(IdentifierKey).Order());

    private static (string Provider, string Type, string Key) IdentifierKey(
        MatchingExternalIdentity value
    ) => (value.Provider.ToLowerInvariant(), value.ResourceType, value.ProviderKey);

    private static bool SameIdentifier(
        MatchingExternalIdentity first,
        MatchingExternalIdentity second
    ) => IdentifierKey(first) == IdentifierKey(second);
}
