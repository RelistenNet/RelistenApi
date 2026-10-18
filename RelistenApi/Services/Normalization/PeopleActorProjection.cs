using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization;

/// <summary>
/// Reproduces the incoming query and comparison from original typed actor assertions. Contacts
/// and profiles remain nonunique hints; this projection never constructs canonical identifiers.
/// Limits are explicit: an omitted query term prevents acceptance rather than proving absence.
/// </summary>
public static class PeopleActorProjection
{
    public const string Version = "people-actor-projection-v1";

    /// <summary>Producer identity for the additional role-free assertion input contract.</summary>
    public const string SourceAliasVersion =
        "people-source-actor-alias-projection-v1/" + SourceActorAliasExtractor.Version;
    private const int MaximumTerms = 16;

    public static PeopleActorProjectionResult Project(
        RetainedCreditMention mention,
        Guid artistUuid
    ) => Project(mention.Name, mention.ActorFacts, artistUuid);

    /// <summary>Projects the same source assertions before a provider-specific evidence remap.</summary>
    public static PeopleActorProjectionResult Project(CreditMention mention, Guid artistUuid) =>
        Project(mention.Name, mention.ActorFacts, artistUuid);

    /// <summary>
    /// Projects the explicit names in one source assertion for proposal comparison. The
    /// source's artist supplies catalog context, never attendance or a recording credit.
    /// This projection supplies no person kind, hints or accepted identity authority.
    /// </summary>
    public static PeopleActorProjectionResult Project<TEvidence>(
        SourceActorAliasAssertion<TEvidence> assertion,
        Guid artistUuid
    )
    {
        var result = Project(
            assertion.Name,
            assertion.Aliases.Select(alias => alias.Wording),
            null,
            null,
            artistUuid
        );
        return result with { Complete = result.Complete && assertion.Unresolved.Count == 0 };
    }

    private static PeopleActorProjectionResult Project<TEvidence>(
        string name,
        CreditedActorFacts<TEvidence>? facts,
        Guid artistUuid
    ) =>
        Project(
            name,
            facts?.Aliases.Select(alias => alias.Wording) ?? [],
            facts?.Kind?.Kind,
            Context(facts == null ? [] : new[] { facts }),
            artistUuid
        );

    private static PeopleActorProjectionResult Project(
        string name,
        IEnumerable<string> aliases,
        string? kind,
        PersonActorMatchContext? context,
        Guid artistUuid
    )
    {
        var terms = new[] { name }.Concat(aliases).Distinct(StringComparer.Ordinal).ToArray();
        var names = terms
            .Where(NormalizationPeopleResolutionDecisionService.CanQueryName)
            .ToArray();
        var contacts = context?.Contacts ?? [];
        var profiles = context?.Profiles ?? [];
        var complete =
            terms.All(NormalizationPeopleResolutionDecisionService.CanQueryName)
            && names.Length <= MaximumTerms
            && contacts.Count <= MaximumTerms
            && profiles.Count <= MaximumTerms
            && contacts.All(PeopleActorLookupContract.Supports)
            && profiles.All(PeopleActorLookupContract.Supports);
        var queryNames = names.Take(MaximumTerms).ToArray();
        var supportedContacts = contacts
            .Where(PeopleActorLookupContract.Supports)
            .Take(MaximumTerms)
            .ToArray();
        var supportedProfiles = profiles
            .Where(PeopleActorLookupContract.Supports)
            .Take(MaximumTerms)
            .ToArray();
        PeopleCandidateHints? hints =
            supportedContacts.Length + supportedProfiles.Length == 0
                ? null
                : new(supportedContacts, supportedProfiles);
        var query =
            queryNames.Length == 0 && hints == null
                ? null
                : new NormalizationCandidateQuery(
                    IdentityMatchingDomain.People,
                    queryNames,
                    [],
                    PeopleHints: hints
                );
        return new(
            query,
            new(
                null,
                kind,
                queryNames.Select(name => new IdentityMatchingName(name)).ToArray(),
                [],
                new HashSet<Guid> { artistUuid },
                context
            ),
            complete
        );
    }

    public static PersonMatchRecord Candidate(PersonCandidateFacts candidate)
    {
        var actors = candidate.ActorEvidence ?? [];
        var matching = candidate.ActorMatchingFacts;
        var names =
            matching == null
                ? actors
                    .Select(actor => actor.CreditedName)
                    .Concat(
                        actors
                            .SelectMany(actor => actor.Facts.Aliases)
                            .Select(alias => alias.Wording)
                    )
                : matching.CreditedNames.Concat(matching.Aliases);
        var context =
            matching == null ? Context(actors.Select(actor => actor.Facts))
            : matching.HasActorContext
                ? new PersonActorMatchContext(
                    matching.Contacts,
                    matching.Profiles,
                    matching.HasAttachmentUncertainty,
                    matching.HasConflictingKind
                )
            : null;
        return new(
            candidate.Hit.Uuid,
            candidate.Header.Kind,
            candidate
                .IdentityClosure.Select(header => new IdentityMatchingName(header.DisplayText))
                .Concat(
                    (
                        candidate.AcceptedAliasMatchingNames
                        ?? candidate.Aliases.Select(alias => alias.Wording)
                    ).Select(wording => new IdentityMatchingName(wording))
                )
                .Concat(names.Select(name => new IdentityMatchingName(name)))
                .ToArray(),
            candidate
                .ExternalIdentities.Select(identity => new MatchingExternalIdentity(
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey
                ))
                .ToArray(),
            candidate.ArtistContexts.Select(context => context.ArtistUuid).ToHashSet(),
            context
        );
    }

    /// <summary>Cache the entire query, including context routes and their limits, under this projection.</summary>
    public static string QueryFingerprint(NormalizationCandidateQuery query) =>
        Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        Version,
                        query,
                        limits = new CandidateFactsLimits(),
                    }
                )
            )
        );

    /// <summary>Authenticates semantic actor facts against this exact supported producer output.</summary>
    public static void RequireOriginalActorFacts(
        IReadOnlyList<NormalizationRecordingCreditProposal> saved,
        IReadOnlyList<NormalizationRecordingCreditProposal> reproduced
    )
    {
        if (saved.Count != reproduced.Count)
        {
            throw new NormalizationConflictException(
                "Actor facts require the complete original credit extraction."
            );
        }
        foreach (var credit in saved)
        {
            var original = reproduced.SingleOrDefault(value =>
                value.ProposalUuid == credit.ProposalUuid
            );
            if (
                original == null
                || !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(original),
                    JsonSerializer.SerializeToElement(credit)
                )
            )
            {
                throw new NormalizationConflictException(
                    "Actor facts do not reproduce their original supported extraction."
                );
            }
        }
    }

    internal static PersonActorMatchContext? Context<TEvidence>(
        IEnumerable<CreditedActorFacts<TEvidence>> source
    )
    {
        var facts = source.ToArray();
        if (facts.Length == 0)
        {
            return null;
        }
        return new(
            facts
                .SelectMany(actor => actor.Contacts)
                .Select(contact => new PeopleContactLookup(contact.ContactType, contact.LookupKey))
                .Distinct()
                .ToArray(),
            facts
                .SelectMany(actor => actor.Profiles)
                .Select(profile => new PeopleProfileLookup(
                    profile.Provider,
                    profile.Namespace,
                    profile.LookupKey,
                    profile.Relationship
                ))
                .Distinct()
                .ToArray(),
            facts.Any(actor =>
                actor.Unresolved.Any(issue =>
                    issue.Reason
                        is "unattributed_contact"
                            or "unattributed_profile"
                            or "ambiguous_actor_attachment"
                            or "actor_text_mapping_unavailable"
                            or "ambiguous_alias_boundary"
                )
            ),
            facts.Any(actor =>
                actor.Unresolved.Any(issue => issue.Reason == "conflicting_actor_kind")
            )
        );
    }
}

public sealed record PeopleActorProjectionResult(
    NormalizationCandidateQuery? Query,
    PersonMatchRecord Record,
    bool Complete
);
