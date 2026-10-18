using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestSourceActorReview
{
    [Test]
    public async Task ExactPublishedClaimRetainsTheOriginalHumanReceiptAndExplicitOutOfPoolTarget()
    {
        var source = await Source();
        var target = await Person("Explicit correspondence target " + Guid.NewGuid());
        var accepted = await Accept(source, target);
        var authority = await AcceptedState();
        var counts = await HumanDecisionCounts(source);
        var resolver = NewSourceResolver();

        var resolved = await resolver.ResolveAsync(Guid.NewGuid(), source.Extraction.AttemptUuid);

        resolved.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var proof = resolved
            .Preparation!.SourceReviews.Correspondences.Should()
            .ContainSingle()
            .Which;
        AssertOriginalHumanApproval(proof, accepted.Preparation, accepted.Operation, target);
        proof.Original.Coordinates.Should().Be(source.Claim);
        proof.Current.Coordinates.Should().Be(source.Claim);
        Claims(resolved)
            .Single()
            .Decision.Should()
            .BeEquivalentTo(
                new SourceActorResolutionDecision(
                    SourceActorResolutionAction.Review,
                    "reviewed_source_acceptance",
                    target.Uuid,
                    Claims(resolved).Single().Decision.PolicyProposal
                )
            );
        resolved
            .Preparation.CandidatePools.SelectMany(pool => pool.Facts)
            .Should()
            .NotContain(fact => fact.Hit.Uuid == target.Uuid);
        await AssertProofOwnersInPublishedTicket(resolved, proof);
        (await HumanDecisionCounts(source)).Should().Be(counts);
        (await AcceptedState()).Should().Be(authority);
    }

    [Test]
    public async Task UnchangedPersonnelFieldCarriesApprovalAcrossAnUnrelatedRawFieldUpdate()
    {
        var source = await Source();
        var target = await Person("Carry forward target " + Guid.NewGuid());
        var accepted = await Accept(source, target);
        await ChangeTaper(source);
        var current = await ExtractSource(source.Source);
        current.Claim.InputRevisionUuid.Should().NotBe(source.Claim.InputRevisionUuid);
        var authority = await AcceptedState();
        var counts = await HumanDecisionCounts(source);

        var resolved = await NewSourceResolver()
            .ResolveAsync(Guid.NewGuid(), current.Extraction.AttemptUuid);

        resolved.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var proof = resolved
            .Preparation!.SourceReviews.Correspondences.Should()
            .ContainSingle()
            .Which;
        AssertOriginalHumanApproval(proof, accepted.Preparation, accepted.Operation, target);
        proof.Original.Coordinates.Should().Be(source.Claim);
        proof.Current.Coordinates.Should().Be(current.Claim);
        proof.Original.FieldText.Should().Be(proof.Current.FieldText);
        proof.LocalIds.Should().NotBeEmpty();
        proof.LocalIds.Select(map => (map.Kind, map.OriginalUuid)).Should().OnlyHaveUniqueItems();
        proof.LocalIds.Select(map => (map.Kind, map.CurrentUuid)).Should().OnlyHaveUniqueItems();
        proof.LocalIds.Should().OnlyContain(map => map.OriginalUuid != map.CurrentUuid);
        Claims(resolved).Single().Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        Claims(resolved).Single().Decision.PersonUuid.Should().Be(target.Uuid);
        await AssertProofOwnersInPublishedTicket(resolved, proof);
        (await HumanDecisionCounts(source)).Should().Be(counts);
        (await AcceptedState()).Should().Be(authority);
    }

    [Test]
    public async Task ChangedSurroundingPersonnelProseRequiresReviewEvenWhenTheActorNamesStayTheSame()
    {
        var source = await Source();
        var target = await Person("Changed prose target " + Guid.NewGuid());
        await Accept(source, target);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description=@description WHERE id=@SourceId",
                new
                {
                    source.Source.SourceId,
                    description = "Personnel:\nReview Robin aka Finch - vocals\nAdditional upstream context.",
                }
            )
        );
        var current = await ExtractSource(source.Source);
        var authority = await AcceptedState();
        var counts = await HumanDecisionCounts(source);

        var resolved = await NewSourceResolver()
            .ResolveAsync(Guid.NewGuid(), current.Extraction.AttemptUuid);

        resolved.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        resolved.Preparation!.SourceReviews.Correspondences.Should().BeNullOrEmpty();
        var claim = Claims(resolved).Single();
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        claim.Decision.Reason.Should().Be("reviewed_source_field_requires_correspondence");
        claim.Decision.PersonUuid.Should().BeNull();
        (await HumanDecisionCounts(source)).Should().Be(counts);
        (await AcceptedState()).Should().Be(authority);
    }

    [Test]
    public async Task ExactCurrentRejectionBlocksAnOlderAcceptedFieldCorrespondence()
    {
        var source = await Source();
        var target = await Person("Negative precedence target " + Guid.NewGuid());
        var accepted = await Accept(source, target);
        await ChangeTaper(source);
        var current = await ExtractSource(source.Source);
        var rejected = await Prepare(current, SourceActorReviewAction.Reject);
        await reviews.ApplyAsync(Guid.NewGuid(), rejected.PreparationUuid, Reason, Operator);
        var counts = await HumanDecisionCounts(source);
        var authority = await AcceptedState();

        var resolved = await NewSourceResolver()
            .ResolveAsync(Guid.NewGuid(), current.Extraction.AttemptUuid);

        resolved.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        resolved.Preparation!.SourceReviews.Correspondences.Should().BeNullOrEmpty();
        resolved
            .Preparation.SourceReviews.Reviews.Should()
            .Contain(review =>
                review.LastOperationUuid == accepted.Operation && review.State == "accepted"
            );
        Claims(resolved).Single().Decision.Action.Should().Be(SourceActorResolutionAction.Rejected);
        Claims(resolved).Single().Decision.PersonUuid.Should().BeNull();
        (await HumanDecisionCounts(source)).Should().Be(counts);
        (await AcceptedState()).Should().Be(authority);
    }

    [Test]
    public async Task TargetOwnerChangeInvalidatesSavedCorrespondenceWithoutChangingPeopleMatchingUniverse()
    {
        var source = await Source();
        var target = await Person("Readonly owner target " + Guid.NewGuid());
        await Accept(source, target);
        var paused = await PauseSourceResolutionBeforeAdmission(source);
        var proof = paused
            .Preparation!.SourceReviews.Correspondences.Should()
            .ContainSingle()
            .Which;
        var targetOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", target.Uuid)
            )
        )!;
        proof
            .Target.OwnerObservations.Should()
            .Contain(owner => owner.SubjectUuid == targetOwner.SubjectUuid);
        var universe = await PeopleMatchingGeneration();
        var revision = await PersonRevision(target.Uuid);
        await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            targetOwner.Subject,
            "changed-owner-configuration-" + Guid.NewGuid(),
            [],
            [],
            expectedConfigurationGeneration: targetOwner.DesiredGeneration
        );
        (await PeopleMatchingGeneration()).Should().Be(universe);
        (await PersonRevision(target.Uuid)).Should().Be(revision);
        var counts = await HumanDecisionCounts(source);
        var authority = await AcceptedState();

        var resumed = await NewSourceResolver()
            .ResolveAsync(paused.AttemptUuid, paused.ExtractionAttemptUuid);

        resumed.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Stale);
        resumed
            .Preparation.Should()
            .BeEquivalentTo(
                paused.Preparation,
                "resuming an attempt must retain its original target observation"
            );
        (await new SourceActorResolutionStore(fixture.Db).GetAttemptBatchAsync(paused.AttemptUuid))
            .Should()
            .BeNull();
        (await HumanDecisionCounts(source)).Should().Be(counts);
        (await AcceptedState()).Should().Be(authority);
    }

    private SourceActorResolutionService NewSourceResolver() =>
        new(
            new(fixture.Db),
            new(fixture.Db, fixture.Work, allowFixturePolicies: true),
            new(new(), new()),
            new(fixture.Db),
            fixture.Work
        );

    private async Task<(SourceActorReviewPreparation Preparation, Guid Operation)> Accept(
        SourceFixture source,
        PersonFixture target
    )
    {
        var preparation = await Prepare(source, SourceActorReviewAction.Accept, target);
        var operation = Guid.NewGuid();
        await reviews.ApplyAsync(operation, preparation.PreparationUuid, Reason, Operator);
        return (preparation, operation);
    }

    private Task ChangeTaper(SourceFixture source) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE sources SET taper='Unknown' WHERE id=@SourceId", source.Source)
        );

    private static IEnumerable<SourceActorResolutionClaim> Claims(
        SourceActorResolutionAttempt attempt
    ) =>
        attempt
            .Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.Claims);

    private static void AssertOriginalHumanApproval(
        ReviewedSourceCorrespondence proof,
        SourceActorReviewPreparation preparation,
        Guid operation,
        PersonFixture target
    )
    {
        proof.OriginalReview.Uuid.Should().Be(preparation.ReviewStateUuid);
        proof.OriginalReview.LastOperationUuid.Should().Be(operation);
        proof.OriginalReview.ReviewRevision.Should().Be(1);
        proof.OriginalReview.SelectedPersonUuid.Should().Be(target.Uuid);
        proof.Target.OriginalPersonUuid.Should().Be(target.Uuid);
        proof.Target.ResolvedPersonUuid.Should().Be(target.Uuid);
        proof.Original.Should().BeEquivalentTo(preparation.OriginalField);
    }

    private async Task AssertProofOwnersInPublishedTicket(
        SourceActorResolutionAttempt attempt,
        ReviewedSourceCorrespondence proof
    )
    {
        var batch = (
            await new SourceActorResolutionStore(fixture.Db).GetAttemptBatchAsync(
                attempt.AttemptUuid
            )
        )!;
        foreach (var owner in proof.Target.OwnerObservations)
        {
            batch.Ticket.OwnerObservations.Should().ContainEquivalentOf(owner);
            var after = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;
            new NormalizationSubjectObservation(
                after.SubjectUuid,
                after.DesiredGeneration,
                after.EditorialRevision,
                after.PublishedWorkUuid
            )
                .Should()
                .Be(owner);
        }
    }

    private Task<HumanCounts> HumanDecisionCounts(SourceFixture source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<HumanCounts>(
                """
                SELECT (SELECT count(*) FROM normalization.review_operations operation
                        JOIN normalization.work_subjects owner ON owner.uuid=operation.subject_uuid
                        WHERE owner.domain='people' AND owner.subject_kind='source_actor_resolution' AND owner.subject_uuid=@SourceUuid) AS "Operations",
                    (SELECT count(*) FROM normalization.source_actor_alias_review_state WHERE source_uuid=@SourceUuid) AS "CurrentRows",
                    (SELECT count(*) FROM normalization.source_actor_alias_review_state_history WHERE source_uuid=@SourceUuid) AS "HistoryRows"
                """,
                source.Source
            )
        );

    private Task<long> PeopleMatchingGeneration() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                """
                SELECT generation FROM normalization.candidate_universe_revisions
                WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'
                """
            )
        );

    private async Task<SourceActorResolutionAttempt> PauseSourceResolutionBeforeAdmission(
        SourceFixture source
    )
    {
        var attemptUuid = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new
            {
                key = $"normalization:configuration:people:source_actor_resolution:{source.Source.SourceUuid}",
            },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = NewSourceResolver()
            .ResolveAsync(attemptUuid, source.Extraction.AttemptUuid, cancellation.Token);
        try
        {
            await WaitForBlocker(blocker.ProcessID);
            var saved = (
                await new SourceActorResolutionStore(fixture.Db).GetAttemptAsync(attemptUuid)
            )!;
            saved.Preparation.Should().NotBeNull();
            saved.Result.Should().BeNull();
            cancellation.Cancel();
            var failure = await ((Func<Task>)(() => pending)).Should().ThrowAsync<Exception>();
            IsCancellation(failure.Which).Should().BeTrue();
            return saved;
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await pending;
            }
            catch (Exception exception) when (IsCancellation(exception))
            {
                // The first immutable preparation remains available for public retry.
            }
        }
    }

    private sealed record HumanCounts(long Operations, long CurrentRows, long HistoryRows);
}
