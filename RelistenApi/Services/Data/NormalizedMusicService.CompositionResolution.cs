using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    /// <summary>
    /// Publishes only the processor's saved admitted stage. Callers provide identifiers, never
    /// winner payloads or policy authority. The domain write and stage receipt commit together.
    /// </summary>
    public async Task<NormalizationPublicationResult> PublishCompositionResolutionAsync(
        Guid workUuid,
        Guid resolutionAttemptUuid,
        CancellationToken ct = default
    )
    {
        var resolutions = new NormalizationCompositionResolutionStore(db);
        var original =
            await resolutions.GetAsync(resolutionAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown composition resolution attempt.");
        var requested = original.Stages.SingleOrDefault(stage => stage.WorkUuid == workUuid);
        // Work's terminal replay skips the callback; bind its public request before entering it.
        if (requested?.Ticket == null)
        {
            throw new NormalizationConflictException(
                "Publication requires this resolution's admitted stage."
            );
        }
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, token) =>
            {
                var stage = await resolutions.RequireStageAsync(
                    c,
                    t,
                    resolutionAttemptUuid,
                    workUuid,
                    requested.Kind,
                    token
                );
                var attempt = (
                    await resolutions.ReadForPublicationAsync(c, t, resolutionAttemptUuid, token)
                )!;
                var proof = attempt.Proposal!;
                RequireCompositionProof(attempt, stage);
                await CompositionResolutionPolicyService.RequireActiveAsync(
                    c,
                    t,
                    proof.Policy!,
                    token
                );
                await RequireCurrentAcceptedClaimAsync(c, t, proof.Claim, token);
                await ValidateOwnership(c, t, stage.Ticket!.SubjectUuid, stage.Publication, token);
                await ValidateDependencies(c, t, stage.Ticket, stage.Publication, token);
                await RequireResolutionEvidence(stage, proof, token);
                await ApplyCompositionResolutionStageAsync(c, t, stage, proof, workUuid, token);
                await resolutions.WriteStagePublishedAsync(
                    c,
                    t,
                    resolutionAttemptUuid,
                    workUuid,
                    new(workUuid, NormalizationPublicationStatus.Published, false),
                    token
                );
            },
            ct,
            candidateWriteScopes: requested.Kind == CompositionResolutionStageKind.CreateComposition
                ? [NormalizationCandidateUniverseKey.Compositions]
                :
                [
                    NormalizationCandidateUniverseKey.Compositions,
                    NormalizationCandidateUniverseKey.ForRepertoire(
                        original.Preparation!.Context.ArtistUuid
                    ),
                ]
        );
    }

    private static async Task RequireCurrentAcceptedClaimAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CatalogWorkClaimReference claim,
        CancellationToken ct
    )
    {
        var sourceCurrent = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM normalization.repertoire_songs WHERE uuid=@RepertoireSongUuid AND revision_uuid=@MusicRevisionUuid AND NOT retracted AND redirect_uuid IS NULL)",
                claim,
                t,
                cancellationToken: ct
            )
        );
        if (!sourceCurrent)
        {
            throw new NormalizationConflictException(
                "The accepted repertoire claim changed after correspondence evaluation."
            );
        }
    }

    private static async Task ApplyCompositionResolutionStageAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompositionResolutionStage stage,
        EvaluatedCatalogWorkCorrespondenceReceipt proof,
        Guid workUuid,
        CancellationToken ct
    )
    {
        if (stage.Kind == CompositionResolutionStageKind.CreateComposition)
        {
            await Apply(c, t, stage.Publication, workUuid, false, ct);
        }
        else
        {
            await LockAndValidateChanges(c, t, stage.Publication, false, ct);
            if (proof.ExpectedLinkRevision > 0)
            {
                // An independently evaluated reattempt can acknowledge the same accepted
                // link without changing its evidence, authority or candidate epoch.
                var same = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM normalization.repertoire_compositions x
                            JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                            WHERE x.uuid=@LinkUuid AND x.repertoire_song_uuid=@song AND x.composition_uuid=@CompositionUuid
                                AND NOT x.retracted AND r.revision=@ExpectedLinkRevision)
                        """,
                        new
                        {
                            proof.LinkUuid,
                            song = proof.Claim.RepertoireSongUuid,
                            proof.CompositionUuid,
                            proof.ExpectedLinkRevision,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (!same)
                {
                    throw new NormalizationConflictException(
                        "The accepted composition assignment changed."
                    );
                }
            }
            else
            {
                var link = stage.Publication.CompositionLinks.Single();
                await RequireUnredirected(c, t, "repertoire_songs", link.RepertoireSongUuid, ct);
                await RequireUnredirected(c, t, "compositions", link.CompositionUuid, ct);
                // This distinct path has authenticated a processor-issued receipt. It never
                // masquerades as an ID asserted by the local source or a complete author list.
                await Persist(
                    c,
                    t,
                    "repertoire_compositions",
                    link,
                    "repertoire_song_uuid,composition_uuid",
                    "@RepertoireSongUuid,@CompositionUuid",
                    workUuid,
                    false,
                    ct
                );
            }
        }
    }

    private static void RequireCompositionProof(
        CompositionResolutionAttempt attempt,
        CompositionResolutionStage stage
    )
    {
        var proof = attempt.Proposal!;
        var context =
            proof.Context
            ?? throw new NormalizationConflictException(
                "The evaluated receipt has no original candidate context."
            );
        if (
            attempt.ProcessingVersion != NormalizationCompositionResolutionProcessor.Version
            || proof.ProcessorVersion != attempt.ProcessingVersion
            || proof.AttemptUuid != attempt.AttemptUuid
            || proof.Claim != attempt.Claim
            || proof.Policy == null
            || proof.ExternalWorkMbid == null
            || proof.CompositionUuid == null
            || proof.Decision.Action != CompositionCorrespondenceAction.ProposeAcceptance
            || JsonSerializer.Serialize(proof.Policy)
                != JsonSerializer.Serialize(attempt.Preparation!.Policy)
            || JsonSerializer.Serialize(proof.Facts.SourceTitle)
                != JsonSerializer.Serialize(attempt.Preparation.Context.LocalTitle)
            || proof.Facts.Artist != attempt.Preparation.Context.Artist
            || context.Candidates.Version != NormalizationCandidateService.FactsVersion
            || JsonSerializer.Serialize(proof.Facts.LocalCandidates)
                != JsonSerializer.Serialize(context.Candidates.Facts)
            || JsonSerializer.Serialize(proof.Facts.LocalRepertoire)
                != JsonSerializer.Serialize(context.Repertoire)
            || JsonSerializer.Serialize(
                new CompositionResolutionPolicyEvaluator().Evaluate(
                    proof.Facts,
                    proof.Policy.Policy
                )
            ) != JsonSerializer.Serialize(proof.Decision)
        )
        {
            throw new NormalizationConflictException(
                "A supported evaluated processor receipt is required."
            );
        }
        if (stage.Kind == CompositionResolutionStageKind.CreateComposition)
        {
            var work = proof
                .Facts.Candidates.Single(candidate =>
                    candidate.ExternalWorkMbid == proof.ExternalWorkMbid
                )
                .Work;
            if (
                proof.ExpectedCompositionRevision != 0
                || stage.Publication.Compositions.Count != 1
                || stage.Publication.Compositions[0].Uuid != proof.CompositionUuid
                || stage.Publication.Compositions[0].DisplayTitle != work.Work.Name?.Value
                || !stage
                    .Publication.Compositions[0]
                    .Evidence.SequenceEqual([work.Work.Name!.Evidence])
                || stage.Publication.Compositions[0].ExpectedRevision != 0
                || stage.Publication.Compositions[0].Retracted
                || stage.Publication.ExternalIdentities.Count != 1
                || stage.Publication.ExternalIdentities[0].CompositionUuid != proof.CompositionUuid
                || stage.Publication.ExternalIdentities[0].Provider != "musicbrainz.org"
                || stage.Publication.ExternalIdentities[0].ResourceType != "work"
                || stage.Publication.ExternalIdentities[0].ProviderKey
                    != proof.ExternalWorkMbid.Value.ToString("D")
                || !stage
                    .Publication.ExternalIdentities[0]
                    .Evidence.SequenceEqual([work.Work.Identity.Evidence])
                || stage.Publication.ExternalIdentities[0].Retracted
                || stage.Publication.CompositionLinks.Count != 0
            )
            {
                throw new NormalizationConflictException(
                    "Composition creation differs from its saved proof."
                );
            }
        }
        else if (proof.ExpectedLinkRevision == 0)
        {
            var link = stage.Publication.CompositionLinks.SingleOrDefault();
            if (
                link == null
                || link.Uuid != proof.LinkUuid
                || link.RepertoireSongUuid != proof.Claim.RepertoireSongUuid
                || link.CompositionUuid != proof.CompositionUuid
                || link.EvaluatedCorrespondenceReceiptUuid != proof.ReceiptUuid
                || link.ExpectedRevision != proof.ExpectedLinkRevision
                || link.MatchReason != "evaluated_correspondence"
                || link.Retracted
                || link.Basis != "validated_match"
                || !link.Evidence.SequenceEqual(
                    proof
                        .Decision.Candidates.Single(candidate =>
                            candidate.ExternalWorkMbid == proof.ExternalWorkMbid
                        )
                        .Evidence
                )
                || link.CompleteAuthorship
                || link.MatchedWorkIdentity != null
                || link.MatchedAuthorUuids.Count != 0
            )
            {
                throw new NormalizationConflictException(
                    "Composition assignment differs from its saved proof."
                );
            }
        }
        var allowed =
            stage.Kind == CompositionResolutionStageKind.CreateComposition ? 2
            : proof.ExpectedLinkRevision == 0 ? 1
            : 0;
        if (
            Changes(stage.Publication).Count() != allowed
            || stage.Publication.Merges.Count != 0
            || stage.Publication.Splits.Count != 0
        )
        {
            throw new NormalizationConflictException(
                "A correspondence stage cannot publish unrelated changes."
            );
        }
        var captured = attempt
            .Checkpoints.SelectMany(checkpoint =>
                (checkpoint.Search?.Pages ?? [])
                    .Select(page => page.Acquisition?.Result)
                    .Concat(
                        (checkpoint.Hydration?.Lookups ?? []).Select(lookup =>
                            lookup.Acquisition?.Result
                        )
                    )
            )
            .OfType<MusicBrainzAcquisitionResult>()
            .Where(result => result.Input != null);
        if (
            captured.Any(result =>
                !stage.ReadSet.Inputs.Any(input =>
                    input.RevisionUuid == result.Input!.InputRevisionUuid
                    && input.Generation == result.ResourceGeneration
                )
            )
        )
        {
            throw new NormalizationConflictException(
                "A composition stage cannot refresh the original acquisition generations."
            );
        }
    }

    private static Task RequireResolutionEvidence(
        CompositionResolutionStage stage,
        EvaluatedCatalogWorkCorrespondenceReceipt proof,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var acceptedClaimEvidence = proof
            .Context!.Repertoire.Titles.Single(title =>
                title.Provenance.MusicRevisionUuid == proof.Claim.MusicRevisionUuid
            )
            .Provenance.Evidence;
        var inputs = stage.ReadSet.Inputs.Select(input => input.RevisionUuid).ToHashSet();
        if (
            Changes(stage.Publication)
                .SelectMany(change => change.Evidence)
                .Any(citation =>
                    !inputs.Contains(citation.InputRevisionUuid)
                    && !acceptedClaimEvidence.Contains(citation)
                )
        )
        {
            throw new NormalizationConflictException(
                "Evidence is neither an original acquisition nor the exact accepted claim citation."
            );
        }
        return Task.CompletedTask;
    }
}
