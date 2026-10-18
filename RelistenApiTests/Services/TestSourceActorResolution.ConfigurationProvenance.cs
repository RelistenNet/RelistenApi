using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestSourceActorResolution
{
    [Test]
    public async Task OrdinaryWorkReusingSavedResolverUuidDoesNotAuthenticateConfigurationLineage()
    {
        var source = await Source(Personnel("Unrelated admission " + Guid.NewGuid().ToString("N")));
        var extraction = await Extract(source);
        var saved = await StageBeforeAdmission(extraction);
        var preparation = saved.Preparation!;
        preparation.ExpectedContributions.Should().BeEmpty();
        var contribution = SourceActorResolutionReadSet.Contributions(preparation).Single();
        var owner = SourceActorResolutionService.Owner(source.SourceUuid);

        // This public admission installs genuine observed bindings, but it is unrelated to
        // the saved resolver attempt and has no source-resolution domain checkpoint.
        var unrelated = await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            owner,
            saved.ProcessingVersion,
            [contribution],
            preparation.ExpectedConfigurationGeneration,
            expectedContributionRevisions: preparation.ExpectedContributions
        );
        var installed = await fixture.Work.GetInputContributionsAsync(owner);
        installed.UnmanagedBaseline.Should().BeNull();
        installed
            .Contributions.Should()
            .ContainSingle()
            .Which.Revision.Should()
            .Be(contribution.Contribution.ExpectedRevision + 1);
        var ordinary = await fixture.Work.BeginWorkAsync(
            saved.AttemptUuid,
            owner,
            saved.ProcessingVersion,
            contribution.Contribution.Resources,
            contribution.Contribution.DependencySubjectUuids,
            installed.Owner!.DesiredGeneration,
            catalogInputs: contribution.CatalogInputs,
            sourceIdentities: contribution.SourceIdentities,
            mappingRegistry: contribution.MappingRegistry,
            expectedInputRevisions: contribution.ExpectedInputRevisions,
            importedVenues: contribution.ImportedVenues,
            candidateUniverses: contribution.CandidateUniverses,
            expectedOwnerObservations: contribution.ExpectedOwnerObservations,
            sourceUuids: contribution.SourceUuids
        );

        // Even the entire returned ticket can coincide. Ordinary admission leaves the
        // contributor revision vector untouched, so those values cannot prove its mode.
        ordinary.Should().BeEquivalentTo(unrelated with { WorkUuid = saved.AttemptUuid });
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
        (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        var before = await AcceptedState();

        var refused = await NewResolver().ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        refused.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Unresolved);
        refused.Result.Reason.Should().Be("unsupported_owner_inputs");
        refused.Preparation.Should().BeNull();
        (await store.GetAttemptBatchAsync(refused.AttemptUuid)).Should().BeNull();
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
        (await store.GetAttemptAsync(saved.AttemptUuid)).Should().BeEquivalentTo(saved);
        (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        (await AcceptedState()).Should().Be(before);
        var replay = await NewResolver().ResolveAsync(refused.AttemptUuid, extraction.AttemptUuid);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be("unsupported_owner_inputs");
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GenuineAdmissionBeforeCheckpointSupportsExactResumeAndNewExtraction(
        bool replaceWithNewExtraction
    )
    {
        var source = await Source(
            Personnel("Admitted configuration " + Guid.NewGuid().ToString("N"))
        );
        var extraction = await Extract(source);
        var saved = await StageBeforeAdmission(extraction);
        var ticket = await Admit(saved);
        var owner = SourceActorResolutionService.Owner(source.SourceUuid);
        var admitted = await fixture.Work.GetInputContributionsAsync(owner);
        var installed = admitted.Contributions.Should().ContainSingle().Which;
        (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        (await store.GetAttemptAsync(saved.AttemptUuid)).Should().BeEquivalentTo(saved);
        var before = await AcceptedState();
        var currentExtraction = replaceWithNewExtraction ? await Extract(source) : extraction;
        var attemptUuid = replaceWithNewExtraction ? Guid.NewGuid() : saved.AttemptUuid;

        var recovered = await NewResolver()
            .ResolveAsync(attemptUuid, currentExtraction.AttemptUuid);

        recovered.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var current = membership.Contributions.Should().ContainSingle().Which;
        current.Contributor.Should().Be(installed.Contributor);
        current.Revision.Should().Be(installed.Revision + (replaceWithNewExtraction ? 1 : 0));
        if (replaceWithNewExtraction)
        {
            recovered
                .Preparation!.ExpectedContributions.Should()
                .Equal(
                    new NormalizationInputContributionRevision(
                        installed.Contributor,
                        installed.Revision
                    )
                );
            recovered
                .Preparation.ExtractionObservation.PublishedWorkUuid.Should()
                .Be(currentExtraction.AttemptUuid);
            (await store.GetAttemptAsync(saved.AttemptUuid)).Should().BeEquivalentTo(saved);
            (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        }
        else
        {
            recovered.Preparation.Should().BeEquivalentTo(saved.Preparation);
            var batch = (await store.GetAttemptBatchAsync(saved.AttemptUuid))!;
            batch.Ticket.Should().BeEquivalentTo(ticket);
            membership.Contributions.Should().BeEquivalentTo(admitted.Contributions);
        }
        (await store.GetAsync(source.SourceUuid))!.AttemptUuid.Should().Be(attemptUuid);
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task PublishedConfigurationSurvivesTrustedReviewAndNewExtractionWithoutRefreshingOldProof()
    {
        var name = "Reviewed configuration " + Guid.NewGuid().ToString("N");
        var selected = await Person(name);
        var source = await Source(Personnel(name));
        var originalExtraction = await Extract(source);
        var first = await resolver.ResolveAsync(Guid.NewGuid(), originalExtraction.AttemptUuid);
        first.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var originalBatch = (await store.GetAttemptBatchAsync(first.AttemptUuid))!;
        var originalPreparation = originalBatch.Preparation;
        originalPreparation.ExpectedContributions.Should().BeEmpty();
        var claim = Claims(originalPreparation).Single();
        var scope = originalPreparation
            .Inputs.Single()
            .Scopes.Single(value => value.Claims.Count != 0);
        var assertion = scope.OriginalScope.Extraction.Assertions[claim.AssertionIndex];
        var owner = SourceActorResolutionService.Owner(source.SourceUuid);
        var publishedMembership = await fixture.Work.GetInputContributionsAsync(owner);
        var reviews = new SourceActorReviewService(fixture.Db, fixture.Work, new(fixture.Db));
        var personRevision = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@selected",
                new { selected }
            )
        );
        var review = await reviews.PrepareAsync(
            Guid.NewGuid(),
            new(
                originalExtraction.AttemptUuid,
                claim.Coordinates,
                SourceActorReviewAction.Accept,
                publishedMembership.Owner!.EditorialRevision,
                0,
                selected,
                personRevision
            )
        );
        var reviewOperation = Guid.NewGuid();

        await reviews.ApplyAsync(
            reviewOperation,
            review.PreparationUuid,
            "The operator verified this existing identity against the complete source field.",
            Operator
        );

        var reviewedMembership = await fixture.Work.GetInputContributionsAsync(owner);
        reviewedMembership.Contributions.Should().BeEquivalentTo(publishedMembership.Contributions);
        reviewedMembership
            .Owner!.EditorialRevision.Should()
            .Be(publishedMembership.Owner.EditorialRevision + 1);
        reviewedMembership
            .Owner.DesiredGeneration.Should()
            .Be(publishedMembership.Owner.DesiredGeneration + 1);
        originalBatch
            .Ticket.DesiredGeneration.Should()
            .NotBe(reviewedMembership.Owner.DesiredGeneration);
        (await store.GetAttemptBatchAsync(first.AttemptUuid))
            .Should()
            .BeEquivalentTo(originalBatch);
        var before = await AcceptedState();
        var nextExtraction = await Extract(source);
        nextExtraction.AttemptUuid.Should().NotBe(originalExtraction.AttemptUuid);
        nextExtraction
            .Preparation.Binding.InputRevisionUuid.Should()
            .Be(originalExtraction.Preparation.Binding.InputRevisionUuid);

        var next = await NewResolver().ResolveAsync(Guid.NewGuid(), nextExtraction.AttemptUuid);

        next.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        next.Preparation!.ExpectedConfigurationGeneration.Should()
            .Be(reviewedMembership.Owner.DesiredGeneration);
        next.Preparation.ExpectedContributions.Should()
            .BeEquivalentTo(
                reviewedMembership.Contributions.Select(
                    value => new NormalizationInputContributionRevision(
                        value.Contributor,
                        value.Revision
                    )
                )
            );
        next.Preparation.ExtractionObservation.PublishedWorkUuid.Should()
            .Be(nextExtraction.AttemptUuid);
        next.Preparation.ExtractionTicket.Should().BeEquivalentTo(nextExtraction.Ticket);
        var retainedReview = next.Preparation.SourceReviews.Reviews.Should().ContainSingle().Which;
        retainedReview.Coordinates.Should().BeEquivalentTo(claim.Coordinates);
        retainedReview.OriginalAssertion.Should().BeEquivalentTo(assertion);
        retainedReview.OriginalScope.Should().BeEquivalentTo(scope.OriginalScope);
        retainedReview.SelectedPersonUuid.Should().Be(selected);
        retainedReview.ReviewRevision.Should().Be(1);
        retainedReview.LastOperationUuid.Should().Be(reviewOperation);
        var nextClaim = Claims(next.Preparation).Single();
        nextClaim.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        nextClaim.Decision.Reason.Should().Be("reviewed_source_acceptance");
        nextClaim.Decision.PersonUuid.Should().Be(selected);
        nextClaim.SourceReviewStateUuid.Should().Be(review.ReviewStateUuid);
        var currentMembership = await fixture.Work.GetInputContributionsAsync(owner);
        var currentContribution = currentMembership.Contributions.Should().ContainSingle().Which;
        var previousContribution = reviewedMembership.Contributions.Single();
        currentContribution.Contributor.Should().Be(previousContribution.Contributor);
        currentContribution.Revision.Should().Be(previousContribution.Revision + 1);
        (await store.GetAsync(source.SourceUuid))!.AttemptUuid.Should().Be(next.AttemptUuid);
        (await store.GetAttemptBatchAsync(first.AttemptUuid))
            .Should()
            .BeEquivalentTo(originalBatch);

        var replay = await NewResolver()
            .ResolveAsync(first.AttemptUuid, originalExtraction.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Preparation.Should().BeEquivalentTo(originalPreparation);
        replay
            .Preparation!.ExtractionObservation.PublishedWorkUuid.Should()
            .Be(originalExtraction.AttemptUuid);
        replay.Preparation.ExpectedContributions.Should().BeEmpty();
        (await fixture.Work.GetInputContributionsAsync(owner))
            .Should()
            .BeEquivalentTo(currentMembership);
        (await AcceptedState()).Should().Be(before);
    }
}
