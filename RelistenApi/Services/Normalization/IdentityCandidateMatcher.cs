using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Compares one retained claim with one canonical candidate. Candidate retrieval, policy
/// evaluation, multi-candidate resolution and transactional publication remain separate steps.
/// Missing facts are features, never invented agreements or contradictions.
/// </summary>
public sealed class IdentityCandidateMatcher
{
    /// <summary>
    /// Retrieves exact person-name or explicit-alias candidates with the same key
    /// used by comparison. Equal keys alone establish no person identity.
    /// </summary>
    public static string PeopleNameCandidateKey(string wording) => NameKey(wording);

    /// <summary>
    /// Retrieves repertoire spellings that differ only at word or punctuation boundaries.
    /// Callers must preserve artist scope, prefer literal candidates and retain all rivals.
    /// This key neither changes identity keys nor qualifies automatic acceptance.
    /// </summary>
    public static string RepertoireTitleBoundaryCandidateKey(string wording) =>
        NameKey(wording).Replace(" ", "", StringComparison.Ordinal);

    /// <summary>Pin this feature contract in people projections and evaluated policies.</summary>
    public const string PeopleActorFeaturesVersion = "people-actor-context-features-v1";

    public IdentityCandidateComparison ComparePeople(
        PersonMatchRecord claim,
        PersonMatchRecord candidate,
        EvaluationBackedIdentityPolicy? policy = null,
        bool hasReviewedExclusion = false
    )
    {
        var pair = new Comparison(
            IdentityMatchingDomain.People,
            candidate.Uuid,
            hasReviewedExclusion
        );
        pair.CompareNames(claim.Names, candidate.Names);
        pair.CompareIdentifiers(claim.ExternalIdentities, candidate.ExternalIdentities);
        pair.CompareValue("person_kind", claim.Kind, candidate.Kind, contradiction: true);

        var artistOverlap = claim.ArtistUuids.Overlaps(candidate.ArtistUuids);
        pair.Add(
            "artist_context",
            claim.ArtistUuids.Count == 0 || candidate.ArtistUuids.Count == 0
                    ? IdentityFeatureAgreement.Missing
                : artistOverlap ? IdentityFeatureAgreement.Agrees
                : IdentityFeatureAgreement.Differs,
            "Shared artists help rank people; different artists do not contradict a global identity."
        );
        CompareActorContext(pair, claim.ActorContext, candidate.ActorContext);

        if (pair.ExactName && pair.QualifiedHandle)
        {
            pair.Propose(IdentityMatchRule.PersonNameAndQualifiedHandle);
        }

        return pair.Finish(policy);
    }

    private static void CompareActorContext(
        Comparison pair,
        PersonActorMatchContext? claim,
        PersonActorMatchContext? candidate
    )
    {
        if (claim == null && candidate == null)
        {
            return;
        }

        pair.CompareSupportingKeys(
            "actor_contact",
            (claim?.Contacts ?? []).ToHashSet(),
            (candidate?.Contacts ?? []).ToHashSet(),
            "Equal contact keys are nonexclusive source context; different contacts do not contradict identity."
        );

        var leftProfiles = claim?.Profiles ?? [];
        var rightProfiles = candidate?.Profiles ?? [];
        var leftKeys = leftProfiles.Select(ProfileKey).ToHashSet();
        var rightKeys = rightProfiles.Select(ProfileKey).ToHashSet();
        pair.CompareSupportingKeys(
            "actor_profile",
            leftKeys,
            rightKeys,
            "Profile keys agree only within their provider and namespace; shared accounts are not entity identifiers."
        );

        var sameRelationship = leftProfiles
            .Select(profile => (Key: ProfileKey(profile), profile.Relationship))
            .Intersect(
                rightProfiles.Select(profile => (Key: ProfileKey(profile), profile.Relationship))
            )
            .Any();
        pair.Add(
            "actor_profile_relationship",
            !leftKeys.Overlaps(rightKeys) ? IdentityFeatureAgreement.Missing
                : sameRelationship ? IdentityFeatureAgreement.Agrees
                : IdentityFeatureAgreement.Differs,
            "Source relationships remain distinct; attributed profiles and owner statements grant no identifier authority here."
        );

        if (claim?.HasAttachmentUncertainty == true || candidate?.HasAttachmentUncertainty == true)
        {
            pair.Add(
                "actor_attachment_uncertainty",
                IdentityFeatureAgreement.Missing,
                "At least one source retains unresolved actor attachment; nearby hints do not establish attribution."
            );
        }
        if (claim?.HasConflictingKind == true || candidate?.HasConflictingKind == true)
        {
            pair.Add(
                "actor_kind_conflict",
                IdentityFeatureAgreement.Conflicts,
                "A source retains conflicting explicit person and group descriptions."
            );
        }
    }

    // Lookup producers normalize provider names. Preserve opaque account-key and namespace
    // case; source relationships are separate features rather than part of account equality.
    private static (string Provider, string Namespace, string LookupKey) ProfileKey(
        PeopleProfileLookup profile
    ) => (profile.Provider.ToLowerInvariant(), profile.Namespace, profile.LookupKey);

    public IdentityCandidateComparison CompareVenues(
        VenueMatchRecord claim,
        VenueMatchRecord candidate,
        EvaluationBackedIdentityPolicy? policy = null,
        bool hasReviewedExclusion = false
    )
    {
        var pair = new Comparison(
            IdentityMatchingDomain.Venues,
            candidate.Uuid,
            hasReviewedExclusion
        );
        pair.CompareNames(
            claim.Names.Select(name => new IdentityMatchingName(name.Wording)).ToArray(),
            candidate.Names.Select(name => new IdentityMatchingName(name.Wording)).ToArray()
        );
        pair.CompareIdentifiers(claim.ExternalIdentities, candidate.ExternalIdentities);
        pair.CompareValue("venue_kind", claim.Kind, candidate.Kind, contradiction: true);
        pair.CompareValue(
            "parent_site",
            claim.ParentVenueUuid,
            candidate.ParentVenueUuid,
            contradiction: true
        );
        var locationAgrees = CompareVenueLocations(pair, claim, candidate);

        CompareVenueHistory(pair, claim, candidate);
        CompareVenueHistory(pair, candidate, claim);

        if (pair.ExactName && locationAgrees)
        {
            pair.Propose(IdentityMatchRule.VenueNameAndAddress);
        }

        return pair.Finish(policy);
    }

    public IdentityCandidateComparison CompareRepertoire(
        RepertoireMatchRecord claim,
        RepertoireMatchRecord candidate,
        EvaluationBackedIdentityPolicy? policy = null,
        bool hasReviewedExclusion = false
    )
    {
        var pair = new Comparison(
            IdentityMatchingDomain.Repertoire,
            candidate.Uuid,
            hasReviewedExclusion
        );
        pair.CompareNames(claim.Titles, candidate.Titles);
        pair.CompareIdentifiers(claim.ExternalIdentities, candidate.ExternalIdentities);
        pair.CompareValue(
            "artist_scope",
            claim.ArtistUuid,
            candidate.ArtistUuid,
            contradiction: true
        );
        var composition = pair.CompareValue(
            "composition",
            claim.CompositionUuid,
            candidate.CompositionUuid,
            contradiction: true
        );

        if (pair.ExactName && composition == IdentityFeatureAgreement.Agrees)
        {
            pair.Propose(IdentityMatchRule.RepertoireTitleAndComposition);
        }

        return pair.Finish(policy);
    }

    public IdentityCandidateComparison CompareCompositions(
        CompositionMatchRecord claim,
        CompositionMatchRecord candidate,
        EvaluationBackedIdentityPolicy? policy = null,
        bool hasReviewedExclusion = false
    )
    {
        var pair = new Comparison(
            IdentityMatchingDomain.Compositions,
            candidate.Uuid,
            hasReviewedExclusion
        );
        pair.CompareNames(claim.Titles, candidate.Titles);
        pair.CompareIdentifiers(claim.ExternalIdentities, candidate.ExternalIdentities);

        var authorship = CompareAuthorship(claim, candidate);
        pair.Add(
            "authorship",
            authorship,
            "Only resolved writer, composer and lyricist identities count as authors."
        );

        if (
            pair.ExactName
            && authorship == IdentityFeatureAgreement.Agrees
            && claim.CompleteAuthorship
            && candidate.CompleteAuthorship
        )
        {
            pair.Propose(IdentityMatchRule.CompositionTitleAndCompleteAuthorship);
        }

        return pair.Finish(policy);
    }

    private static IdentityFeatureAgreement CompareAuthorship(
        CompositionMatchRecord claim,
        CompositionMatchRecord candidate
    )
    {
        if (claim.AuthorUuids.Count == 0 || candidate.AuthorUuids.Count == 0)
        {
            return IdentityFeatureAgreement.Missing;
        }

        if (
            (claim.CompleteAuthorship && !candidate.AuthorUuids.IsSubsetOf(claim.AuthorUuids))
            || (
                candidate.CompleteAuthorship && !claim.AuthorUuids.IsSubsetOf(candidate.AuthorUuids)
            )
        )
        {
            return IdentityFeatureAgreement.Conflicts;
        }

        return claim.AuthorUuids.Overlaps(candidate.AuthorUuids)
            ? IdentityFeatureAgreement.Agrees
            : IdentityFeatureAgreement.Differs;
    }

    private static bool CompareVenueLocations(
        Comparison pair,
        VenueMatchRecord claim,
        VenueMatchRecord candidate
    )
    {
        if (claim.LocationAssertions == null && candidate.LocationAssertions == null)
        {
            // Existing callers assert one location rather than a dated history. Keep that
            // comparison contract while facts readers adopt the complete collection.
            pair.CompareValue(
                "country",
                claim.Location?.CountryCode,
                candidate.Location?.CountryCode,
                contradiction: true
            );
            var locality = pair.CompareValue(
                "locality",
                claim.Location?.LocalityKey,
                candidate.Location?.LocalityKey,
                contradiction: true
            );
            var address = pair.CompareValue(
                "address",
                VenueAddressKey(claim.Location?.Address),
                VenueAddressKey(candidate.Location?.Address)
            );
            return locality == IdentityFeatureAgreement.Agrees
                && address == IdentityFeatureAgreement.Agrees;
        }

        // Two observations on different days do not supply one date for filtering a history.
        var date =
            claim.ObservedDate != null
            && candidate.ObservedDate != null
            && claim.ObservedDate != candidate.ObservedDate
                ? null
                : claim.ObservedDate ?? candidate.ObservedDate;
        var left = RelevantVenueLocations(claim, date);
        var right = RelevantVenueLocations(candidate, date);
        var countryAgreement = CompareVenueLocationField(
            pair,
            "country",
            left,
            right,
            location => location.CountryCode,
            contradiction: true
        );
        var localityAgreement = CompareVenueLocationField(
            pair,
            "locality",
            left,
            right,
            location => location.LocalityKey,
            contradiction: true
        );
        var addressAgreement = CompareVenueLocationField(
            pair,
            "address",
            left,
            right,
            location => VenueAddressKey(location.Address)
        );
        // A resolved locality does not erase conflicting country evidence. Missing country
        // information remains allowed because the location rule requires locality and address.
        var agrees =
            countryAgreement
                is not (IdentityFeatureAgreement.Differs or IdentityFeatureAgreement.Conflicts)
            && localityAgreement == IdentityFeatureAgreement.Agrees
            && addressAgreement == IdentityFeatureAgreement.Agrees;
        pair.Add(
            "location_history",
            agrees ? IdentityFeatureAgreement.Agrees : IdentityFeatureAgreement.Missing,
            agrees
                ? "Every possible location account supplies the same locality and address."
                : "Known country differences, location alternatives or missing fields prevent the location rule."
        );
        return agrees;
    }

    private static MatchingVenueLocationAssertion[] RelevantVenueLocations(
        VenueMatchRecord record,
        DateOnly? date
    )
    {
        // The record date filters a complete history; it does not date every undated member.
        // A legacy single location is an observation, so its known date supplies a point interval.
        var assertions =
            record.LocationAssertions
            ?? (
                record.Location == null
                    ? []
                    : new[]
                    {
                        new MatchingVenueLocationAssertion(
                            record.Location,
                            record.ObservedDate,
                            record.ObservedDate
                        ),
                    }
            );
        return assertions
            .Where(assertion =>
                date == null || !OutsideVenueLocationInterval(assertion, date.Value)
            )
            .ToArray();
    }

    private static IdentityFeatureAgreement CompareVenueLocationField(
        Comparison pair,
        string name,
        IReadOnlyList<MatchingVenueLocationAssertion> left,
        IReadOnlyList<MatchingVenueLocationAssertion> right,
        Func<MatchingVenueLocation, string?> select,
        bool contradiction = false
    )
    {
        var locations = left.Concat(right).ToArray();
        string? Key(MatchingVenueLocationAssertion assertion)
        {
            var value = select(assertion.Location);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        var known = locations
            .Select(location => (Location: location, Key: Key(location)))
            .Where(value => value.Key != null)
            .ToArray();
        var distinct = known
            .Select(value => value.Key)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var conflict =
            contradiction
            && distinct > 1
            && known.Any(first =>
                known.Any(second =>
                    !StringComparer.OrdinalIgnoreCase.Equals(first.Key, second.Key)
                    && VenueLocationsProvenConcurrent(first.Location, second.Location)
                )
            );
        var missing = left.Count == 0 || right.Count == 0 || known.Length != locations.Length;
        var agreement =
            conflict ? IdentityFeatureAgreement.Conflicts
            : distinct > 1 ? IdentityFeatureAgreement.Differs
            : missing ? IdentityFeatureAgreement.Missing
            : IdentityFeatureAgreement.Agrees;
        pair.Add(
            name,
            agreement,
            conflict ? "Supported concurrent locations assert incompatible resolved geography."
                : agreement == IdentityFeatureAgreement.Missing
                    ? "One or more possible location accounts lack this fact."
                : agreement == IdentityFeatureAgreement.Agrees
                    ? "Every possible location account agrees."
                : "Possible locations differ; unknown dates or unequal address wording do not prove different physical sites."
        );
        return agreement;
    }

    private static string? VenueAddressKey(string? address)
    {
        if (address == null)
        {
            return null;
        }
        var key = NameKey(address);
        return key.Length == 0 ? null : key;
    }

    private static bool VenueLocationsProvenConcurrent(
        MatchingVenueLocationAssertion first,
        MatchingVenueLocationAssertion second
    ) =>
        first.ValidFrom.HasValue
        && first.ValidTo.HasValue
        && second.ValidFrom.HasValue
        && second.ValidTo.HasValue
        && first.ValidFrom.Value <= second.ValidTo.Value
        && second.ValidFrom.Value <= first.ValidTo.Value;

    private static bool OutsideVenueLocationInterval(
        MatchingVenueLocationAssertion assertion,
        DateOnly date
    ) => assertion.ValidFrom is { } from && date < from || assertion.ValidTo is { } to && date > to;

    private static void CompareVenueHistory(
        Comparison pair,
        VenueMatchRecord observed,
        VenueMatchRecord history
    )
    {
        if (observed.ObservedDate is not { } date)
        {
            return;
        }

        var names = history
            .Names.Where(name =>
                observed.Names.Any(other => NameKey(other.Wording) == NameKey(name.Wording))
            )
            .ToArray();
        if (names.Length == 0)
        {
            return;
        }

        // An unknown interval prevents a contradiction. Null bounds must not become a claim
        // that the name was valid forever, nor proof that it was invalid on this date.
        var outsideEveryKnownInterval = names.All(name =>
            (name.ValidFrom.HasValue && date < name.ValidFrom.Value)
            || (name.ValidTo.HasValue && date > name.ValidTo.Value)
        );
        var withinKnownInterval = names.Any(name =>
            name.ValidFrom.HasValue
            && name.ValidTo.HasValue
            && date >= name.ValidFrom.Value
            && date <= name.ValidTo.Value
        );
        pair.Add(
            "name_history",
            outsideEveryKnownInterval ? IdentityFeatureAgreement.Conflicts
                : withinKnownInterval ? IdentityFeatureAgreement.Agrees
                : IdentityFeatureAgreement.Missing,
            outsideEveryKnownInterval
                ? "The observed name falls outside every supported validity interval."
                : "The available name history does not contradict this date."
        );
    }

    private static string NameKey(string value, bool foldDiacritics = false)
    {
        var text = value.Normalize(
            foldDiacritics ? NormalizationForm.FormD : NormalizationForm.FormC
        );
        var key = new StringBuilder();
        var separator = false;

        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (foldDiacritics && category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune) || category == UnicodeCategory.NonSpacingMark)
            {
                if (separator && key.Length > 0)
                {
                    key.Append(' ');
                }

                key.Append(Rune.ToLowerInvariant(rune).ToString());
                separator = false;
            }
            else
            {
                separator = true;
            }
        }

        return key.ToString();
    }

    /// <summary>Collects common pair features; domain methods retain the rules that use them.</summary>
    private sealed class Comparison(
        IdentityMatchingDomain domain,
        Guid? candidateUuid,
        bool excluded
    )
    {
        private readonly List<IdentityMatchingFeature> features = [];
        private readonly List<IdentityMatchRule> rules = [];
        private bool isCandidate;
        private double nameSimilarity;
        public bool ExactName { get; private set; }
        public bool QualifiedHandle { get; private set; }

        public void Add(string name, IdentityFeatureAgreement agreement, string reason)
        {
            features.Add(new(name, agreement, reason));
        }

        public void CompareSupportingKeys<T>(
            string name,
            IReadOnlySet<T> leftKeys,
            IReadOnlySet<T> rightKeys,
            string reason
        )
        {
            var shared = leftKeys.Overlaps(rightKeys);
            // Supporting hints admit a pair for comparison without proposing any acceptance
            // rule. Equal contacts or project accounts can belong to several different people.
            isCandidate |= shared;
            Add(
                name,
                leftKeys.Count == 0 || rightKeys.Count == 0 ? IdentityFeatureAgreement.Missing
                    : shared ? IdentityFeatureAgreement.Agrees
                    : IdentityFeatureAgreement.Differs,
                reason
            );
        }

        public IdentityFeatureAgreement CompareValue<T>(
            string name,
            T? left,
            T? right,
            bool contradiction = false
        )
        {
            if (
                left is null
                || right is null
                || (left is string leftText && string.IsNullOrWhiteSpace(leftText))
                || (right is string rightText && string.IsNullOrWhiteSpace(rightText))
            )
            {
                Add(name, IdentityFeatureAgreement.Missing, "One or both records lack this fact.");
                return IdentityFeatureAgreement.Missing;
            }

            var equal =
                left is string first && right is string second
                    ? StringComparer.OrdinalIgnoreCase.Equals(first, second)
                    : EqualityComparer<T>.Default.Equals(left, right);
            var agreement =
                equal ? IdentityFeatureAgreement.Agrees
                : contradiction ? IdentityFeatureAgreement.Conflicts
                : IdentityFeatureAgreement.Differs;
            var reason =
                equal ? "The retained values agree."
                : contradiction ? "The records assert incompatible values."
                : "The wording differs; this alone does not prove different identities.";
            Add(name, agreement, reason);
            return agreement;
        }

        public void CompareNames(
            IReadOnlyList<IdentityMatchingName> left,
            IReadOnlyList<IdentityMatchingName> right
        )
        {
            var namesLeft = left.Where(name => !name.IsHandle && NameKey(name.Wording).Length > 0)
                .ToArray();
            var namesRight = right
                .Where(name => !name.IsHandle && NameKey(name.Wording).Length > 0)
                .ToArray();
            ExactName = namesLeft.Any(first =>
                namesRight.Any(second => NameKey(first.Wording) == NameKey(second.Wording))
            );
            QualifiedHandle = left.Any(first =>
                first.IsHandle
                && !string.IsNullOrWhiteSpace(first.Provider)
                && right.Any(second =>
                    second.IsHandle
                    && StringComparer.OrdinalIgnoreCase.Equals(first.Provider, second.Provider)
                    && StringComparer.Ordinal.Equals(first.Wording, second.Wording)
                )
            );
            var hasNames = namesLeft.Length > 0 && namesRight.Length > 0;
            Add(
                "name",
                !hasNames ? IdentityFeatureAgreement.Missing
                    : ExactName ? IdentityFeatureAgreement.Agrees
                    : IdentityFeatureAgreement.Differs,
                "Name keys retain diacritics and all letter/number tokens; names alone do not establish identity."
            );
            Add(
                "qualified_handle",
                QualifiedHandle
                    ? IdentityFeatureAgreement.Agrees
                    : IdentityFeatureAgreement.Missing,
                "Only equal handles within the same provider agree; a handle alone is insufficient."
            );

            foreach (var first in namesLeft)
            {
                foreach (var second in namesRight)
                {
                    var a = NameKey(first.Wording, foldDiacritics: true)
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .ToHashSet(StringComparer.Ordinal);
                    var b = NameKey(second.Wording, foldDiacritics: true)
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .ToHashSet(StringComparer.Ordinal);
                    var union = a.Union(b).Count();
                    nameSimilarity = Math.Max(
                        nameSimilarity,
                        union == 0 ? 0 : (double)a.Intersect(b).Count() / union
                    );
                }
            }

            isCandidate |= ExactName || QualifiedHandle || nameSimilarity > 0;
        }

        public void CompareIdentifiers(
            IReadOnlyList<MatchingExternalIdentity> left,
            IReadOnlyList<MatchingExternalIdentity> right
        )
        {
            var shared = (
                from first in left
                from second in right
                where SameIdentifier(first, second)
                select (first, second)
            ).ToArray();
            var entityIdentifier = shared.Any(pair =>
                pair.first.IdentifiesEntity && pair.second.IdentifiesEntity
            );
            Add(
                "external_identifier",
                shared.Length > 0 ? IdentityFeatureAgreement.Agrees
                    : left.Count == 0 || right.Count == 0 ? IdentityFeatureAgreement.Missing
                    : IdentityFeatureAgreement.Differs,
                "Identifiers are qualified by provider and resource type. Unequal page IDs can be duplicate records."
            );
            var exclusiveLeft = left.Where(identity =>
                    identity.IdentifiesEntity && identity.ExclusiveWithinEntity
                )
                .ToArray();
            var exclusiveRight = right
                .Where(identity => identity.IdentifiesEntity && identity.ExclusiveWithinEntity)
                .ToArray();
            var exclusiveConflict = exclusiveLeft.Any(first =>
                exclusiveRight.Any(second =>
                    StringComparer.OrdinalIgnoreCase.Equals(first.Provider, second.Provider)
                    && first.ResourceType == second.ResourceType
                    && first.ProviderKey != second.ProviderKey
                )
            );
            if (exclusiveConflict)
            {
                Add(
                    "exclusive_identifier",
                    IdentityFeatureAgreement.Conflicts,
                    "An authority that permits one current entity ID supplies different identifiers."
                );
            }

            isCandidate |= shared.Length > 0;

            if (entityIdentifier)
            {
                Propose(IdentityMatchRule.QualifiedEntityIdentifier);
            }
        }

        private static bool SameIdentifier(
            MatchingExternalIdentity first,
            MatchingExternalIdentity second
        )
        {
            return StringComparer.OrdinalIgnoreCase.Equals(first.Provider, second.Provider)
                && StringComparer.Ordinal.Equals(first.ResourceType, second.ResourceType)
                && StringComparer.Ordinal.Equals(first.ProviderKey, second.ProviderKey);
        }

        public void Propose(IdentityMatchRule supportedRule)
        {
            rules.Add(supportedRule);
        }

        public IdentityCandidateComparison Finish(EvaluationBackedIdentityPolicy? policy)
        {
            if (excluded)
            {
                Add(
                    "reviewed_exclusion",
                    IdentityFeatureAgreement.Conflicts,
                    "A current reviewed exclusion prohibits this relationship."
                );
            }

            var conflicts = features.Any(feature =>
                feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
            var enabledRule = FindEnabledRule(policy);
            var enabled = enabledRule.HasValue && candidateUuid.HasValue;
            var disposition = IdentityMatchDisposition.NeedsEvidence;
            if (conflicts)
            {
                disposition = IdentityMatchDisposition.Review;
            }
            else if (!isCandidate)
            {
                disposition = IdentityMatchDisposition.NotCandidate;
            }
            else if (enabled)
            {
                disposition = IdentityMatchDisposition.AutomaticMatch;
            }

            return new(
                candidateUuid,
                domain,
                disposition,
                isCandidate,
                nameSimilarity,
                features.ToArray(),
                rules.ToArray(),
                enabledRule ?? rules.Select(rule => (IdentityMatchRule?)rule).FirstOrDefault(),
                enabled && !conflicts ? policy!.Version : null,
                enabled && !conflicts ? policy!.EvaluationReceiptUuid : null
            );
        }

        private IdentityMatchRule? FindEnabledRule(EvaluationBackedIdentityPolicy? policy)
        {
            if (
                policy is null
                || policy.Domain != domain
                || string.IsNullOrWhiteSpace(policy.Version)
                || policy.EvaluationReceiptUuid == Guid.Empty
            )
            {
                return null;
            }

            foreach (var rule in rules)
            {
                if (policy.EnabledRules.Contains(rule))
                {
                    return rule;
                }
            }

            return null;
        }
    }
}
