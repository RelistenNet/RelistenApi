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

        if (pair.ExactName && pair.QualifiedHandle)
        {
            pair.Propose(IdentityMatchRule.PersonNameAndQualifiedHandle);
        }

        return pair.Finish(policy);
    }

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
            claim.Location?.Address,
            candidate.Location?.Address,
            wording: true
        );

        CompareVenueHistory(pair, claim, candidate);
        CompareVenueHistory(pair, candidate, claim);

        if (
            pair.ExactName
            && locality == IdentityFeatureAgreement.Agrees
            && address == IdentityFeatureAgreement.Agrees
        )
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

        public IdentityFeatureAgreement CompareValue<T>(
            string name,
            T? left,
            T? right,
            bool contradiction = false,
            bool wording = false
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
                    ? wording
                        ? NameKey(first) == NameKey(second)
                        : StringComparer.OrdinalIgnoreCase.Equals(first, second)
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
