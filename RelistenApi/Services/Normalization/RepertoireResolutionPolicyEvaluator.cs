using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization;

/// <summary>
/// Evaluates a claim page against its complete artist pool and shared source titles. New decision
/// and identity UUIDs are allocation proposals, checkpointed by the processor before publication.
/// Pair results alone do not authorize publication.
/// </summary>
public sealed partial class RepertoireResolutionPolicyEvaluator
{
    public const string ProcessorVersion = "repertoire-resolution-processor-v1";
    public const string SourceVersion = "repertoire-resolution-source-v1";
    public const string MatcherVersion =
        "repertoire-pair-features-v2/" + ArtistSongTitleResolver.Version;

    public static bool Supports(RepertoireAcceptancePolicyArtifact artifact) =>
        artifact.ProcessorVersion == NormalizationRepertoireResolutionProcessor.Version
        && artifact.SourceVersion == SourceVersion
        && artifact.CandidateFactsVersion == NormalizationCandidateService.FactsVersion
        && artifact.MatcherVersion == MatcherVersion
        && artifact.EnabledRules.Count > 0
        && artifact.EnabledRules.Distinct().Count() == artifact.EnabledRules.Count
        && artifact.EnabledRules.All(rule =>
            rule
                is IdentityMatchRule.QualifiedEntityIdentifier
                    or IdentityMatchRule.ArtistScopedNormalizedTitle
        );

    private RepertoireResolutionClaimDecision DecideWithoutTitle(
        RepertoireResolutionClaimContext context,
        ActiveRepertoireAcceptancePolicy? policy,
        ExpertKnowledgeEvaluation? knowledge
    )
    {
        var snapshot = context.Candidates;
        var claim = context.Claim;
        if (snapshot?.Query.ArtistUuid is not { } artist)
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Unresolved,
                "source_artist_unbound"
            );
        }
        if (
            !snapshot.Complete
            || snapshot.Version != NormalizationCandidateService.FactsVersion
            || snapshot.Facts.Any(candidate => candidate.ArtistUuid != artist)
            || !snapshot
                .Selection.Items.Select(item => item.Uuid)
                .ToHashSet()
                .SetEquals(snapshot.Facts.Select(candidate => candidate.Header.Uuid))
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Unresolved,
                "incomplete_candidate_context"
            );
        }
        if (claim.Kind is not ("dictionary" or "song"))
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Unresolved,
                "unsupported_claim_kind"
            );
        }
        if (
            string.IsNullOrWhiteSpace(claim.Title?.Value)
            || !claim.Title.Value.Any(char.IsLetterOrDigit)
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Unresolved,
                "missing_or_unsupported_title"
            );
        }
        if (RelevantExclusion(context, artist))
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "reviewed_music_exclusion"
            );
        }

        // A reviewed tombstone remains authority over that relationship. Allocating another
        // assertion UUID must not recreate it; an explicit later active assertion is distinct.
        var imported = context
            .ImportedAssignments.Where(value => value.ImportedSongUuid == claim.ImportedSongUuid)
            .ToArray();
        if (
            imported.Any(value => value.Retracted && value.Basis == "reviewed")
            && !imported.Any(value => !value.Retracted)
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "reviewed_imported_assignment_retracted"
            );
        }
        if (
            claim.Identities.Any(identity =>
                context.ExistingExternalIdentities.Any(value =>
                    value.Retracted
                    && value.Basis == "reviewed"
                    && SameIdentity(identity.Identity, value)
                )
                && !context.ExistingExternalIdentities.Any(value =>
                    !value.Retracted && SameIdentity(identity.Identity, value)
                )
            )
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "reviewed_external_identity_retracted"
            );
        }

        if (ReviewedKnowledgeDecision(context, knowledge) is { } reviewed)
        {
            return reviewed;
        }
        var excluded = KnowledgeTargets(context, knowledge?.ExcludedTargets ?? []).ToHashSet();
        if (excluded.Count > 0)
        {
            // A negative review excludes its named accepted target. It does not reject every
            // rival or erase the original candidate evidence retained in the page context.
            snapshot = snapshot with
            {
                Facts = snapshot
                    .Facts.Where(candidate => !excluded.Contains(candidate.Header.Uuid))
                    .ToArray(),
            };
        }

        var identities = claim
            .Identities.Concat(claim.AliasOf is { } alias ? [alias] : [])
            .ToArray();
        var identified = snapshot
            .Facts.Where(candidate =>
                identities.Any(identity =>
                    candidate.ExternalIdentities.Any(value =>
                        SameIdentity(identity.Identity, value)
                    )
                )
            )
            .ToArray();
        if (identified.Length > 1)
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "qualified_identity_conflict"
            );
        }
        if (
            claim.AliasOf is { } aliasOf
            && !snapshot.Facts.Any(candidate =>
                candidate.ExternalIdentities.Any(identity =>
                    SameIdentity(aliasOf.Identity, identity)
                )
            )
        )
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "alias_target_unresolved"
            );
        }
        var importedTargets = context
            .ImportedAssignments.Where(assignment =>
                assignment.ImportedSongUuid == claim.ImportedSongUuid && !assignment.Retracted
            )
            .Select(assignment => assignment.RepertoireSongUuid)
            .Distinct()
            .ToArray();
        if (importedTargets.Length > 1)
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "imported_assignment_conflict"
            );
        }
        Guid? previous =
            context.ExistingAssociation?.RepertoireSongUuid
            ?? (importedTargets.Length == 1 ? importedTargets[0] : null);
        if (previous != null)
        {
            var retained = snapshot
                .Facts.Where(candidate =>
                    candidate.Header.Uuid == previous
                    || candidate.IdentityClosure.Any(identity => identity.Uuid == previous)
                )
                .ToArray();
            if (
                retained.Length != 1
                || identified.Any(candidate => candidate.Header.Uuid != retained[0].Header.Uuid)
            )
            {
                return Decision(
                    context,
                    RepertoireResolutionClaimDisposition.Review,
                    "retained_correspondence_conflict"
                );
            }
            if (
                context.ExistingAssociation is
                {
                    Disposition: RepertoireResolutionClaimDisposition.Review
                        or RepertoireResolutionClaimDisposition.Unresolved
                }
            )
            {
                return Decision(
                    context,
                    RepertoireResolutionClaimDisposition.Review,
                    "prior_correspondence_requires_review"
                );
            }
            if (ReviewedMetadataConflict(claim, retained[0], policy))
            {
                return Decision(
                    context,
                    RepertoireResolutionClaimDisposition.Review,
                    "reviewed_metadata_conflict"
                );
            }
            return Decision(
                context,
                context.ExistingAssociation?.Disposition
                == RepertoireResolutionClaimDisposition.Provisional
                    ? RepertoireResolutionClaimDisposition.Provisional
                    : RepertoireResolutionClaimDisposition.Bound,
                "retained_source_correspondence",
                retained[0].Header.Uuid,
                "retained_correspondence"
            );
        }
        if (identified.Length == 1)
        {
            if (policy == null || !Supports(policy.Policy.Artifact))
            {
                return Decision(
                    context,
                    RepertoireResolutionClaimDisposition.Review,
                    "acceptance_policy_inactive_or_unsupported"
                );
            }
            var candidate = identified[0];
            if (ReviewedMetadataConflict(claim, candidate, policy))
            {
                return Decision(
                    context,
                    RepertoireResolutionClaimDisposition.Review,
                    "reviewed_metadata_conflict"
                );
            }
            var source = new RepertoireMatchRecord(
                null,
                artist,
                claim.Title == null ? [] : [new(claim.Title.Value)],
                identities
                    .Select(identity => new MatchingExternalIdentity(
                        identity.Identity.Provider,
                        identity.Identity.ResourceType,
                        identity.Identity.ProviderKey,
                        IdentifiesEntity: true
                    ))
                    .ToArray()
            );
            var target = new RepertoireMatchRecord(
                candidate.Header.Uuid,
                candidate.ArtistUuid,
                candidate
                    .Titles.SelectMany(title => title.Aliases.Prepend(title.DisplayTitle))
                    .Select(title => new IdentityMatchingName(title))
                    .ToArray(),
                candidate
                    .ExternalIdentities.Select(identity => new MatchingExternalIdentity(
                        identity.Provider,
                        identity.ResourceType,
                        identity.ProviderKey,
                        IdentifiesEntity: true
                    ))
                    .ToArray()
            );
            var artifact = policy.Policy.Artifact;
            var comparison = new IdentityCandidateMatcher().CompareRepertoire(
                source,
                target,
                new(
                    artifact.Version,
                    policy.Policy.Evaluation.Uuid,
                    IdentityMatchingDomain.Repertoire,
                    artifact.EnabledRules.ToHashSet()
                )
            );
            if (comparison.Disposition != IdentityMatchDisposition.AutomaticMatch)
            {
                return Decision(
                    context,
                    RepertoireResolutionClaimDisposition.Review,
                    "qualified_identity_not_supported"
                );
            }
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Bound,
                claim.AliasOf == null
                    ? "qualified_provider_song_identity"
                    : "qualified_provider_alias",
                candidate.Header.Uuid,
                "evaluated_qualified_identity"
            );
        }
        if (claim.AliasOf != null)
        {
            return Decision(
                context,
                RepertoireResolutionClaimDisposition.Review,
                "alias_target_unresolved"
            );
        }
        // A clear unbound title may remain visibly provisional. Its new UUID does not assert
        // equivalence with an existing same-title song and does not identify a global work.
        return claim.Kind == "dictionary"
            ? Decision(
                context,
                RepertoireResolutionClaimDisposition.Bound,
                "new_provider_dictionary_member",
                Guid.NewGuid(),
                "provider_dictionary_assertion"
            )
            : Decision(
                context,
                RepertoireResolutionClaimDisposition.Provisional,
                "new_title_derived_identity",
                Guid.NewGuid(),
                "provisional_title"
            );
    }

    private static bool RelevantExclusion(RepertoireResolutionClaimContext context, Guid artist)
    {
        var claim = context.Claim;
        var previous = context.ExistingAssociation?.RepertoireSongUuid;
        return context.ReviewedExclusions.Any(publication =>
            publication.RepertoireSongs.Any(song =>
                song.ArtistUuid == artist
                && (
                    song.Uuid == previous
                    || song.DisplayTitle == claim.Title?.Value
                        && song.Evidence.Any(citation =>
                            citation == claim.Origin.Evidence || citation == claim.Title?.Evidence
                        )
                )
            )
            || publication.ImportedSongs.Any(assignment =>
                assignment.ImportedSongUuid == claim.ImportedSongUuid
            )
            || publication.ExternalIdentities.Any(identity =>
                claim.Identities.Any(value =>
                    value.Identity.Provider == identity.Provider
                    && value.Identity.ResourceType == identity.ResourceType
                    && value.Identity.ProviderKey == identity.ProviderKey
                )
            )
        );
    }

    private static bool ReviewedMetadataConflict(
        RepertoireResolutionClaim claim,
        RepertoireCandidateFacts target,
        ActiveRepertoireAcceptancePolicy? policy
    )
    {
        if (claim.Kind != "dictionary" || target.Header.MetadataReviewed != true)
        {
            return false;
        }
        var current = target.Titles.SingleOrDefault(title =>
            title.Provenance.StoredIdentityUuid == target.Header.Uuid
        );
        if (current == null)
        {
            return true;
        }
        if (SupportsTitleReuse(policy))
        {
            // Reusing a reviewed song must remain valid on the next refresh. The evaluated
            // title rule permits equivalent spelling without granting a metadata overwrite.
            var names = current
                .Aliases.Prepend(current.DisplayTitle)
                .Select(ArtistSongTitleResolver.NormalizeTitle)
                .ToHashSet(StringComparer.Ordinal);
            return claim.Title is { } observed
                    && !names.Contains(ArtistSongTitleResolver.NormalizeTitle(observed.Value))
                || claim.Aliases.Any(alias =>
                    !names.Contains(ArtistSongTitleResolver.NormalizeTitle(alias.Value))
                );
        }
        var unsupportedTitle =
            claim.Title is { } title
            && title.Value != current.DisplayTitle
            && (claim.AliasOf == null || !current.Aliases.Contains(title.Value));
        return unsupportedTitle
            || claim.Aliases.Any(alias =>
                alias.Value != current.DisplayTitle && !current.Aliases.Contains(alias.Value)
            );
    }

    private static bool SameIdentity(
        NormalizationResourceKey left,
        MusicExternalIdentityChange right
    ) =>
        left.Provider == right.Provider
        && left.ResourceType == right.ResourceType
        && left.ProviderKey == right.ProviderKey;

    private static bool SameIdentity(
        NormalizationResourceKey left,
        CandidateExternalIdentityFact right
    ) =>
        left.Provider == right.Provider
        && left.ResourceType == right.ResourceType
        && left.ProviderKey == right.ProviderKey;

    private static RepertoireResolutionClaimDecision Decision(
        RepertoireResolutionClaimContext context,
        RepertoireResolutionClaimDisposition disposition,
        string reason,
        Guid? target = null,
        string authority = "unresolved"
    )
    {
        var claim = context.Claim;
        var evidence = new[] { claim.Origin.Evidence }
            .Concat(claim.Title is { } title ? [title.Evidence] : [])
            .Concat(claim.Identities.Select(identity => identity.Evidence))
            .Concat(claim.Aliases.Select(alias => alias.Evidence))
            .Concat(claim.AliasOf is { } targetIdentity ? [targetIdentity.Evidence] : [])
            .Distinct()
            .ToArray();
        return new(
            Guid.NewGuid(),
            claim.ClaimUuid,
            claim.CorrespondenceKey,
            disposition,
            reason,
            target,
            evidence,
            authority,
            context.Candidates?.Facts.Select(value => value.Header.Uuid).Order().ToArray() ?? []
        );
    }
}
