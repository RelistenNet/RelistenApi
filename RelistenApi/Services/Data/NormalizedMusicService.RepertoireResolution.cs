using System;
using System.Collections.Generic;
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
    /// The saved processor stage supplies all decisions and writes. Bind identifiers before
    /// entering Work because a terminal replay intentionally skips its publication callback.
    /// </summary>
    public async Task<NormalizationPublicationResult> PublishRepertoireResolutionAsync(
        Guid workUuid,
        Guid resolutionAttemptUuid,
        Guid stageUuid,
        CancellationToken ct = default
    )
    {
        var resolutions = new NormalizationRepertoireResolutionStore(db);
        var original =
            await resolutions.GetAsync(resolutionAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown repertoire resolution attempt.");
        var requested = await resolutions.GetStageAsync(resolutionAttemptUuid, stageUuid, ct);
        if (
            requested?.Ticket == null
            || requested.WorkUuid != workUuid
            || requested.Superseded
            || requested.FailureReason != null
            || requested.Kind
                is not (
                    RepertoireResolutionStageKind.AcceptedWrite
                    or RepertoireResolutionStageKind.RetireArtistContribution
                )
            || requested.Owner.Domain != "songs"
            || requested.Owner.Kind != "artist_repertoire"
            || requested.Owner.Uuid == Guid.Empty
            || requested.Context.ArtistUuid != requested.Owner.Uuid
            || original.Preparation?.Source.ArtistUuid != requested.Owner.Uuid
        )
        {
            throw new NormalizationConflictException(
                "Publication requires this repertoire resolution's admitted artist stage."
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
                    stageUuid,
                    workUuid,
                    requested.Kind,
                    token
                );
                var attempt = (
                    await resolutions.ReadForPublicationAsync(
                        c,
                        t,
                        resolutionAttemptUuid,
                        token,
                        stageUuid
                    )
                )!;
                RequireRepertoireArtistStage(attempt, stage);

                if (stage.Kind == RepertoireResolutionStageKind.AcceptedWrite)
                {
                    RequireRepertoireAcceptedProof(attempt, stage);
                    await RepertoireResolutionPolicyService.RequireStateAsync(
                        c,
                        t,
                        stage.Context.Policy,
                        token
                    );
                    await RequireRepertoireEvidenceAsync(c, t, stage, token);
                    await ValidateOwnership(
                        c,
                        t,
                        stage.Ticket!.SubjectUuid,
                        stage.Publication,
                        token
                    );
                    await ValidateDependencies(c, t, stage.Ticket, stage.Publication, token);
                    // Apply preserves normal revision CAS, review authority, claim exclusions
                    // and exact retained-evidence validation for every accepted row.
                    await Apply(c, t, stage.Publication, workUuid, false, token);
                    await resolutions.WriteAssociationsAsync(c, t, attempt, stage, token);
                }
                else
                {
                    await RequireRepertoireRetirementAsync(c, t, attempt, stage, token);
                    await ValidateOwnership(
                        c,
                        t,
                        stage.Ticket!.SubjectUuid,
                        stage.Publication,
                        token
                    );
                }

                await resolutions.WriteStagePublishedAsync(
                    c,
                    t,
                    resolutionAttemptUuid,
                    stageUuid,
                    new(workUuid, NormalizationPublicationStatus.Published, false),
                    token
                );
            },
            ct,
            candidateWriteScopes: requested.Kind == RepertoireResolutionStageKind.AcceptedWrite
                ?
                [
                    NormalizationCandidateUniverseKey.Compositions,
                    NormalizationCandidateUniverseKey.ForRepertoire(requested.Owner.Uuid),
                ]
                : []
        );
    }

    private static void RequireRepertoireArtistStage(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage stage
    )
    {
        if (
            attempt.Preparation == null
            || stage.Owner.Domain != "songs"
            || stage.Owner.Kind != "artist_repertoire"
            || stage.Owner.Uuid == Guid.Empty
            || stage.Context.ArtistUuid != stage.Owner.Uuid
            || attempt.Preparation.Source.ArtistUuid != stage.Owner.Uuid
        )
        {
            throw new NormalizationConflictException(
                "A repertoire artist stage requires its authenticated source artist owner."
            );
        }
    }

    private static void RequireRepertoireAcceptedProof(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage stage
    )
    {
        var page = attempt.Stages.SingleOrDefault(value =>
            value.Kind == RepertoireResolutionStageKind.DecisionPage
            && value.PageIndex == stage.PageIndex
        );
        if (
            stage.FailureReason != null
            || stage.ContextOmitted
            || page == null
            || page.ContextOmitted
            || !SameRepertoireArtifact(page.Context, stage.Context)
            || !SameRepertoireArtifact(page.Decisions, stage.Decisions)
        )
        {
            throw new NormalizationConflictException(
                "Accepted repertoire publication requires its complete original decision page."
            );
        }

        RequireRepertoirePageDecisions(attempt, page);
        RequireRepertoireReadSet(page.ReadSet, stage.ReadSet);

        var publication = stage.Publication;
        if (
            !Changes(publication).Any()
            || publication.Compositions.Count
                + publication.CompositionLinks.Count
                + publication.WorkCredits.Count
                + (publication.AuthorshipAssertions?.Count ?? 0)
                + publication.Editions.Count
                + publication.Occurrences.Count
                + publication.TrackContents.Count
                + publication.Merges.Count
                + publication.Splits.Count
                > 0
            || publication.SetlistStructure != null
            || publication.ExternalIdentities.Any(identity =>
                identity.RepertoireSongUuid == null || identity.CompositionUuid != null
            )
            || !SameRepertoireArtifact(
                publication,
                NormalizationRepertoireResolutionProcessor.BuildPublication(
                    stage.Context,
                    stage.Decisions
                )
            )
        )
        {
            throw new NormalizationConflictException(
                "The saved publication is not the restricted repertoire write supported by its decisions."
            );
        }
    }

    /// <summary>
    /// Re-evaluates a saved page without assuming that its accepted stage is still unfinished.
    /// Both music publication and final source acknowledgment use this proof; persisting a
    /// caller-supplied stage alone never supplies matching authority.
    /// </summary>
    internal static void RequireRepertoirePageDecisions(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage page
    )
    {
        var preparation =
            attempt.Preparation
            ?? throw new NormalizationConflictException(
                "Repertoire decisions require their original preparation."
            );
        if (
            attempt.ProcessingVersion != NormalizationRepertoireResolutionProcessor.Version
            || preparation.Source.SourceVersion != RepertoireResolutionPolicyEvaluator.SourceVersion
            || page.Kind != RepertoireResolutionStageKind.DecisionPage
            || page.ContextOmitted
            || page.FailureReason != null
            || page.Owner
                != NormalizationRepertoireResolutionProcessor.ObservationOwner(
                    attempt.Registration.RegistrationUuid
                )
            || !SameRepertoireArtifact(page.Publication, new MusicPublication())
            || preparation.PageSize <= 0
            || page.PageIndex < 0
            || page.PageIndex
                >= Math.Max(
                    1,
                    (preparation.Source.Claims.Count + preparation.PageSize - 1)
                        / preparation.PageSize
                )
            || page.Context.ArtistUuid != preparation.Source.ArtistUuid
            || !SameRepertoireArtifact(page.Context.Policy, preparation.Policy)
        )
        {
            throw new NormalizationConflictException(
                "Repertoire decisions require their complete original source page and policy state."
            );
        }

        var sourceClaims = preparation
            .Source.Claims.Skip(checked(page.PageIndex * preparation.PageSize))
            .Take(preparation.PageSize)
            .ToArray();
        if (
            !SameRepertoireArtifact(
                sourceClaims,
                page.Context.Claims.Select(context => context.Claim).ToArray()
            )
            || page.Decisions.Count != sourceClaims.Length
            || !page
                .Decisions.Select(decision => decision.ClaimUuid)
                .ToHashSet()
                .SetEquals(sourceClaims.Select(claim => claim.ClaimUuid))
            || page.Decisions.Select(decision => decision.DecisionUuid).Distinct().Count()
                != sourceClaims.Length
            || page.Decisions.Any(decision => decision.DecisionUuid == Guid.Empty)
        )
        {
            throw new NormalizationConflictException(
                "The saved repertoire decisions do not cover their exact original claim page."
            );
        }

        RequireRepertoireReadSet(preparation.Source.ReadSet, page.ReadSet);
        RequireRepertoireReadSet(page.Context.ReadSet, page.ReadSet);
        if (!page.ReadSet.OwnerObservations.Contains(page.Context.Policy.OwnerObservation))
        {
            throw new NormalizationConflictException(
                "Repertoire decisions omitted their original policy owner observation."
            );
        }

        var evaluator = new RepertoireResolutionPolicyEvaluator();
        var expectedDecisions = evaluator
            .DecidePage(
                page.Context.Claims,
                page.Context.Policy.Active,
                page.Decisions.Where(value => value.RepertoireSongUuid.HasValue)
                    .ToDictionary(
                        value => value.ClaimUuid,
                        value => value.RepertoireSongUuid!.Value
                    ),
                page.Context.ExpertKnowledge
            )
            .ToDictionary(value => value.ClaimUuid);
        var allocated = new HashSet<Guid>();
        foreach (var context in page.Context.Claims)
        {
            var saved = page.Decisions.Single(decision =>
                decision.ClaimUuid == context.Claim.ClaimUuid
            );
            if (context.Candidates?.Query.ArtistUuid != page.Context.ArtistUuid)
            {
                throw new NormalizationConflictException(
                    "The repertoire candidate context belongs to another artist."
                );
            }

            var expected = expectedDecisions[context.Claim.ClaimUuid] with
            {
                DecisionUuid = saved.DecisionUuid,
            };
            if (expected.Authority is "provider_dictionary_assertion" or "provisional_title")
            {
                var target = saved.RepertoireSongUuid;
                var existing = page.Context.Claims.Any(value =>
                    value.ExistingSongs.Any(song => song.Uuid == target)
                    || (value.Candidates?.Facts ?? []).Any(candidate =>
                        candidate.Header.Uuid == target
                        || candidate.IdentityClosure.Any(identity => identity.Uuid == target)
                    )
                );
                var materialization = attempt.Stages.SingleOrDefault(value =>
                    value.Kind == RepertoireResolutionStageKind.AcceptedWrite
                    && value.PageIndex == page.PageIndex
                );
                if (
                    target == null
                    || target == Guid.Empty
                    || existing
                    || !allocated.Add(target.Value)
                    || materialization == null
                    || materialization.ContextOmitted
                    || materialization.FailureReason != null
                    || !SameRepertoireArtifact(materialization.Context, page.Context)
                    || !SameRepertoireArtifact(materialization.Decisions, page.Decisions)
                    || !materialization.Publication.RepertoireSongs.Any(song =>
                        song.Uuid == target
                        && song.ExpectedRevision == 0
                        && !song.Retracted
                        && song.ArtistUuid == page.Context.ArtistUuid
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "A new repertoire decision requires its saved, unallocated creation identity."
                    );
                }

                expected = expected with { RepertoireSongUuid = target };
            }

            if (!SameRepertoireArtifact(expected, saved))
            {
                throw new NormalizationConflictException(
                    "The saved repertoire decision differs from its evaluated source and candidate facts."
                );
            }
        }
    }

    /// <summary>
    /// Source extraction/history and accepted candidate revisions may cite historical bytes.
    /// Authenticate those citations through their saved source or accepted revision; never
    /// replace their provenance with a newly read raw-resource head.
    /// </summary>
    private static async Task RequireRepertoireEvidenceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionStage stage,
        CancellationToken ct
    )
    {
        var allowed = new HashSet<NormalizedEvidenceReference>();
        var provenance = stage
            .Context.Claims.Where(context =>
                stage.Decisions.Any(decision =>
                    decision.ClaimUuid == context.Claim.ClaimUuid
                    && decision.RepertoireSongUuid.HasValue
                )
            )
            .SelectMany(context =>
                (context.Candidates?.Facts ?? []).SelectMany(candidate =>
                    candidate
                        .Titles.Select(title => title.Provenance)
                        .Concat(
                            candidate.ExternalIdentities.Select(identity => identity.Provenance)
                        )
                )
            )
            .ToArray();
        await RequireAcceptedRepertoireCitationsAsync(c, t, provenance, ct);
        allowed.UnionWith(provenance.SelectMany(assertion => assertion.Evidence));

        foreach (var context in stage.Context.Claims)
        {
            var claim = context.Claim;
            allowed.Add(claim.Origin.Evidence);
            if (claim.Title != null)
            {
                allowed.Add(claim.Title.Evidence);
            }
            allowed.UnionWith(claim.Aliases.Select(alias => alias.Evidence));
            allowed.UnionWith(claim.Identities.Select(identity => identity.Evidence));
            if (claim.AliasOf != null)
            {
                allowed.Add(claim.AliasOf.Evidence);
            }

            // These are current accepted payloads captured with the page owner observation.
            // Their revision CAS is rechecked by Apply when the builder includes an update.
            allowed.UnionWith(context.ExistingSongs.SelectMany(song => song.Evidence));
            allowed.UnionWith(
                context.ImportedAssignments.SelectMany(assignment => assignment.Evidence)
            );
            allowed.UnionWith(
                context.ExistingExternalIdentities.SelectMany(identity => identity.Evidence)
            );
        }

        if (
            Changes(stage.Publication)
                .SelectMany(change => change.Evidence)
                .Any(citation => !allowed.Contains(citation))
        )
        {
            throw new NormalizationConflictException(
                "Repertoire publication cites evidence outside its exact source and accepted candidate context."
            );
        }
    }

    private static async Task RequireAcceptedRepertoireCitationsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<CandidateAssertionProvenance> provenance,
        CancellationToken ct
    )
    {
        var revisionUuids = provenance
            .Select(assertion => assertion.MusicRevisionUuid)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var retained = (
            await c.QueryAsync<RepertoireAcceptedCitation>(
                new CommandDefinition(
                    "SELECT uuid AS Uuid,entity_uuid AS EntityUuid,revision AS Revision,evidence::text AS Evidence FROM normalization.music_revisions WHERE uuid=ANY(@revisionUuids)",
                    new { revisionUuids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(row => row.Uuid);
        foreach (var assertion in provenance)
        {
            if (
                assertion.MusicRevisionUuid is not { } revisionUuid
                || !retained.TryGetValue(revisionUuid, out var revision)
                || revision.EntityUuid != assertion.AssertionUuid
                || revision.Revision != assertion.Revision
                || !SameRepertoireArtifact(
                    JsonSerializer.Deserialize<IReadOnlyList<NormalizedEvidenceReference>>(
                        revision.Evidence
                    ),
                    assertion.Evidence
                )
            )
            {
                throw new NormalizationConflictException(
                    "Candidate evidence does not match its exact retained accepted music revision."
                );
            }
        }
    }

    private static async Task RequireRepertoireRetirementAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage stage,
        CancellationToken ct
    )
    {
        if (
            stage.Decisions.Count != 0
            || !SameRepertoireArtifact(stage.Publication, new MusicPublication())
            || stage.ContextOmitted
            || stage.Context.ArtistOwner == null
            || stage.Context.Claims.Count != 0
            || !SameRepertoireArtifact(stage.Context.ReadSet, RepertoireResolutionReadSets.Empty)
        )
        {
            throw new NormalizationConflictException(
                "Contribution retirement requires an empty saved artist publication."
            );
        }

        var contributor = NormalizationRepertoireResolutionProcessor.ArtistContributionKey(
            stage.Owner.Uuid
        );
        // Admission installed this attempt's transient contributor before publication. A failed
        // admitted write still owns that cleanup obligation, without acquiring a music receipt.
        var write = attempt.Stages.SingleOrDefault(value =>
            value.Kind == RepertoireResolutionStageKind.AcceptedWrite
            && value.PageIndex == stage.PageIndex
            && (
                value.Receipt?.Status == NormalizationPublicationStatus.Published
                || value.Ticket != null && value.Receipt == null && value.FailureReason != null
            )
        );
        var expectedRevision =
            write == null || write.ContextOmitted
                ? (long?)null
                : NormalizationRepertoireResolutionProcessor.MaterializerRevisionAfterAdmissions(
                    attempt,
                    write
                );
        var prior = stage.Context.ArtistOwner.Membership.Contributions.SingleOrDefault(state =>
            state.Contributor == contributor
        );
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            stage.Owner,
            new(),
            ct
        );
        var current = membership.Contributions.SingleOrDefault(state =>
            state.Contributor == contributor
        );
        if (
            write == null
            || write.ContextOmitted
            || write.Context.ArtistOwner == null
            || write.Ticket == null
            || write.Ticket.SubjectUuid != stage.Ticket!.SubjectUuid
            || write.Owner != stage.Owner
            || prior == null
            || prior.Revision != expectedRevision
            || current == null
            || current.SubjectUuid != stage.Ticket!.SubjectUuid
            || current.Revision != checked(prior.Revision + 1)
            || current.Resources.Count
                + current.DependencySubjectUuids.Count
                + current.CatalogArtistUuids.Count
                + current.SourceIdentifiers.Count
                + (current.ImportedVenueUuids?.Count ?? 0)
                + (current.CandidateUniverses?.Count ?? 0)
                + (current.OwnerObservationSubjectUuids?.Count ?? 0)
                + (current.SourceUuids?.Count ?? 0)
                > 0
            || current.UsesMappingRegistry
        )
        {
            throw new NormalizationConflictException(
                "Retirement did not release exactly this saved materializer contribution revision."
            );
        }
    }

    private static void RequireRepertoireReadSet(
        RepertoireResolutionReadSet original,
        RepertoireResolutionReadSet stage
    )
    {
        if (
            original.Inputs.Any(value => !stage.Inputs.Contains(value))
            || original.Dependencies.Any(value => !stage.Dependencies.Contains(value))
            || original.CatalogInputs.Any(value => !stage.CatalogInputs.Contains(value))
            || original.SourceIdentities.Any(value => !stage.SourceIdentities.Contains(value))
            || original.MappingRegistry != null && original.MappingRegistry != stage.MappingRegistry
            || original.ImportedVenues.Any(value => !stage.ImportedVenues.Contains(value))
            || original.CandidateUniverses.Any(value => !stage.CandidateUniverses.Contains(value))
            || original.OwnerObservations.Any(value => !stage.OwnerObservations.Contains(value))
            || original.SourceUuids.Any(value => !stage.SourceUuids.Contains(value))
        )
        {
            throw new NormalizationConflictException(
                "Repertoire publication omitted or refreshed an original read-set family."
            );
        }
    }

    private static bool SameRepertoireArtifact<T>(T left, T right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private sealed record RepertoireAcceptedCitation(
        Guid Uuid,
        Guid EntityUuid,
        long Revision,
        string Evidence
    );
}
