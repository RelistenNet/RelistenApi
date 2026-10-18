using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionProcessor
{
    /// <summary>
    /// A saved decision allocates identity once. Relationship UUIDs derive from that allocation's
    /// nonce and qualified relationship purpose, never from a song title or passage position.
    /// </summary>
    internal static MusicPublication BuildPublication(
        RepertoireResolutionPageContext context,
        IReadOnlyList<RepertoireResolutionClaimDecision> decisions
    )
    {
        var songs = new Dictionary<Guid, RepertoireSongChange>();
        var imported = new Dictionary<Guid, ImportedSongAssignmentChange>();
        var external = new Dictionary<NormalizationResourceKey, MusicExternalIdentityChange>();
        foreach (var decision in decisions)
        {
            if (
                decision.RepertoireSongUuid is not { } target
                || decision.Disposition
                    is RepertoireResolutionClaimDisposition.Review
                        or RepertoireResolutionClaimDisposition.Unresolved
            )
            {
                continue;
            }
            var source = context.Claims.Single(x => x.Claim.ClaimUuid == decision.ClaimUuid);
            var previous = source.ExistingSongs.SingleOrDefault(x => x.Uuid == target);
            if (songs.TryGetValue(target, out var proposed))
            {
                previous = proposed;
            }
            var change = ProposeSong(context.ArtistUuid!.Value, source, previous, decision);
            if (change != null)
            {
                songs[target] = change;
            }
            AddImportedAssignment(imported, source, decision);
            AddExternalIdentities(external, source, decision);
        }
        return new()
        {
            RepertoireSongs = songs.Values.ToArray(),
            ImportedSongs = imported.Values.ToArray(),
            ExternalIdentities = external.Values.ToArray(),
        };
    }

    private static void AddImportedAssignment(
        Dictionary<Guid, ImportedSongAssignmentChange> imported,
        RepertoireResolutionClaimContext source,
        RepertoireResolutionClaimDecision decision
    )
    {
        var claim = source.Claim;
        var target = decision.RepertoireSongUuid!.Value;
        if (claim.ImportedSongUuid is { } importedUuid)
        {
            var assignment = source.ImportedAssignments.SingleOrDefault(x =>
                x.ImportedSongUuid == importedUuid && !x.Retracted
            );
            if (assignment == null || !ResolvesTo(source, assignment.RepertoireSongUuid, target))
            {
                imported[importedUuid] = new()
                {
                    Uuid =
                        assignment?.Uuid
                        ?? RelationshipUuid(decision.DecisionUuid, "imported-song"),
                    ExpectedRevision = assignment?.ExpectedRevision ?? 0,
                    ImportedSongUuid = importedUuid,
                    RepertoireSongUuid = target,
                    Basis = "provider_assertion",
                    Evidence = decision.Evidence,
                };
            }
        }
    }

    private static void AddExternalIdentities(
        Dictionary<NormalizationResourceKey, MusicExternalIdentityChange> external,
        RepertoireResolutionClaimContext source,
        RepertoireResolutionClaimDecision decision
    )
    {
        var claim = source.Claim;
        var target = decision.RepertoireSongUuid!.Value;
        foreach (var identity in claim.Identities)
        {
            var key = identity.Identity;
            var existing = source.ExistingExternalIdentities.SingleOrDefault(x =>
                !x.Retracted
                && x.Provider == key.Provider
                && x.ResourceType == key.ResourceType
                && x.ProviderKey == key.ProviderKey
            );
            if (existing != null)
            {
                if (
                    existing.RepertoireSongUuid is not { } storedTarget
                    || !ResolvesTo(source, storedTarget, target)
                )
                {
                    throw new NormalizationConflictException(
                        "The checked provider song ID already supports another accepted identity."
                    );
                }
                continue;
            }
            if (external.TryGetValue(key, out var earlier))
            {
                if (earlier.RepertoireSongUuid != target)
                {
                    throw new NormalizationConflictException(
                        "Conflicting source claims cannot allocate two targets for one qualified song identity."
                    );
                }
                continue;
            }
            external[key] = new()
            {
                Uuid = RelationshipUuid(
                    decision.DecisionUuid,
                    "external:" + key.Provider + ":" + key.ResourceType + ":" + key.ProviderKey
                ),
                RepertoireSongUuid = target,
                Provider = key.Provider,
                ResourceType = key.ResourceType,
                ProviderKey = key.ProviderKey,
                Basis = "provider_assertion",
                Evidence = [identity.Evidence],
            };
        }
    }

    private static RepertoireSongChange? ProposeSong(
        Guid artistUuid,
        RepertoireResolutionClaimContext context,
        RepertoireSongChange? previous,
        RepertoireResolutionClaimDecision decision
    )
    {
        var claim = context.Claim;
        if (previous == null)
        {
            return new()
            {
                Uuid = decision.RepertoireSongUuid!.Value,
                ArtistUuid = artistUuid,
                DisplayTitle = claim.Title!.Value,
                Aliases = claim
                    .Aliases.Select(x => x.Value)
                    .Distinct(StringComparer.Ordinal)
                    .Where(x => x != claim.Title.Value)
                    .ToArray(),
                Basis =
                    decision.Authority == "provider_dictionary_assertion"
                        ? "provider_assertion"
                        : "title_derived",
                Evidence = decision.Evidence,
            };
        }
        var usesRetiredIdentityCorrespondence =
            claim.AliasOf == null
            && (
                context.ImportedAssignments.Any(value =>
                    value.ImportedSongUuid == claim.ImportedSongUuid
                    && value.RepertoireSongUuid != previous.Uuid
                    && ResolvesTo(context, value.RepertoireSongUuid, previous.Uuid)
                )
                || context.ExistingExternalIdentities.Any(value =>
                    value.RepertoireSongUuid is { } stored
                    && stored != previous.Uuid
                    && claim.Identities.Any(identity =>
                        identity.Identity.Provider == value.Provider
                        && identity.Identity.ResourceType == value.ResourceType
                        && identity.Identity.ProviderKey == value.ProviderKey
                    )
                    && ResolvesTo(context, stored, previous.Uuid)
                )
            );
        if (claim.Kind != "dictionary" || usesRetiredIdentityCorrespondence)
        {
            // A retained ancestor correspondence permits reuse of its survivor, but old source
            // metadata cannot rename that survivor. Other mentions likewise add no music
            // revision or candidate epoch merely because the source was reprocessed.
            return null;
        }
        var title =
            claim.AliasOf == null && claim.Title != null
                ? claim.Title.Value
                : previous.DisplayTitle;
        var aliases = previous
            .Aliases.Concat(claim.Aliases.Select(x => x.Value))
            .Concat(claim.AliasOf != null && claim.Title != null ? [claim.Title.Value] : [])
            .Concat(title != previous.DisplayTitle ? [previous.DisplayTitle] : [])
            .Distinct(StringComparer.Ordinal)
            .Where(x => x != title)
            .ToArray();
        var basis = previous.Basis == "title_derived" ? "provider_assertion" : previous.Basis;
        if (
            title == previous.DisplayTitle
            && aliases.SequenceEqual(previous.Aliases)
            && basis == previous.Basis
        )
        {
            return null;
        }
        return previous with
        {
            DisplayTitle = title,
            Aliases = aliases,
            Basis = basis,
            Evidence = previous.Evidence.Concat(decision.Evidence).Distinct().ToArray(),
        };
    }

    private async Task<NormalizationPublicationResult> PublishAcknowledgmentAsync(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage requested,
        CancellationToken ct
    )
    {
        if (
            requested.Ticket == null
            || requested.Owner != ObservationOwner(attempt.Registration.RegistrationUuid)
        )
        {
            throw new NormalizationConflictException(
                "A source acknowledgment requires its saved observation owner and admitted ticket."
            );
        }
        return await work.TryPublishAsync(
            requested.WorkUuid,
            async (c, t, token) =>
            {
                var stage = await store.RequireStageAsync(
                    c,
                    t,
                    attempt.AttemptUuid,
                    requested.StageUuid,
                    requested.WorkUuid,
                    RepertoireResolutionStageKind.AcknowledgeSource,
                    token
                );
                var original = (
                    await store.ReadForPublicationAsync(c, t, attempt.AttemptUuid, token)
                )!;
                if (
                    original.ProcessingVersion != Version
                    || stage.Owner != ObservationOwner(original.Registration.RegistrationUuid)
                )
                {
                    throw new NormalizationConflictException(
                        "The observation acknowledgment belongs to another processor or source registration."
                    );
                }
                RequireOriginalReadSet(original.Preparation!.Source.ReadSet, stage.ReadSet);
                if (original.Stages.Any(value => value.FailureReason != null))
                {
                    throw new NormalizationConflictException(
                        "A failed artist stage cannot acknowledge source completion."
                    );
                }
                await RepertoireResolutionPolicyService.RequireStateAsync(
                    c,
                    t,
                    original.Preparation.Policy,
                    token
                );
                await RequireDecisionPagesAsync(c, t, original, token);
                await store.WriteAssociationsAsync(c, t, original, stage, token);
                await store.WriteStagePublishedAsync(
                    c,
                    t,
                    original.AttemptUuid,
                    stage.StageUuid,
                    new(stage.WorkUuid, NormalizationPublicationStatus.Published, false),
                    token
                );
            },
            ct,
            candidateWriteScopes: []
        );
    }

    private async Task RequireDecisionPagesAsync(
        Npgsql.NpgsqlConnection c,
        Npgsql.NpgsqlTransaction t,
        RepertoireResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        foreach (
            var page in attempt.Stages.Where(x =>
                x.Kind == RepertoireResolutionStageKind.DecisionPage
            )
        )
        {
            var proof =
                await store.ReadForPublicationAsync(c, t, attempt.AttemptUuid, ct, page.StageUuid)
                ?? throw new NormalizationConflictException(
                    "The saved source decision page is unavailable."
                );
            var full = proof.Stages.Single(value => value.StageUuid == page.StageUuid);
            NormalizedMusicService.RequireRepertoirePageDecisions(proof, full);
            if (
                HasChanges(BuildPublication(full.Context, full.Decisions))
                && !attempt.Stages.Any(x =>
                    x.Kind == RepertoireResolutionStageKind.AcceptedWrite
                    && x.PageIndex == page.PageIndex
                    && x.Receipt?.Status == NormalizationPublicationStatus.Published
                )
            )
            {
                throw new NormalizationConflictException(
                    "A changed repertoire decision has no committed accepted stage."
                );
            }
        }
    }

    private static void RequireOriginalReadSet(
        RepertoireResolutionReadSet source,
        RepertoireResolutionReadSet stage
    )
    {
        if (
            !source.Inputs.All(stage.Inputs.Contains)
            || !source.Dependencies.All(stage.Dependencies.Contains)
            || !source.CatalogInputs.All(stage.CatalogInputs.Contains)
            || !source.SourceIdentities.All(stage.SourceIdentities.Contains)
            || source.MappingRegistry != null && source.MappingRegistry != stage.MappingRegistry
            || !source.ImportedVenues.All(stage.ImportedVenues.Contains)
            || !source.CandidateUniverses.All(stage.CandidateUniverses.Contains)
            || !source.OwnerObservations.All(stage.OwnerObservations.Contains)
            || !source.SourceUuids.All(stage.SourceUuids.Contains)
        )
        {
            throw new NormalizationConflictException(
                "Source acknowledgment must preserve every original source observation."
            );
        }
    }

    private static bool ResolvesTo(
        RepertoireResolutionClaimContext context,
        Guid stored,
        Guid resolved
    ) =>
        stored == resolved
        || (context.Candidates?.Facts ?? []).Any(candidate =>
            candidate.Header.Uuid == resolved
            && candidate.IdentityClosure.Any(identity => identity.Uuid == stored)
        );

    private static Guid RelationshipUuid(Guid decisionUuid, string purpose)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(decisionUuid.ToString("D") + ":" + purpose)
        );
        return new Guid(hash.AsSpan(0, 16));
    }
}
