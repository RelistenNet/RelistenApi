using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestSourceActorReview
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task ReturnedInputBytesCannotAuthorizeAnOldPositiveReview(bool beforePreparation)
    {
        var source = await Source();
        var person = await Person("Input round-trip target " + Guid.NewGuid());
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var original = (
            await evidence.GetCurrentInputAsync(source.Extraction.Preparation.Binding.Resource)
        )!;
        var prepared = beforePreparation
            ? null
            : await Prepare(source, SourceActorReviewAction.Accept, person);
        var sourceOwner = (
            await fixture.Work.GetSubjectAsync(SourceOwner(source.Source.SourceUuid))
        )!;
        var operation = Guid.NewGuid();
        var changed = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                original.Resource,
                System.Text.Encoding.UTF8.GetBytes("{\"changed\":true}"),
                original.Revision.MediaType,
                original.Revision.CaptureVersion,
                original.Generation,
                DateTimeOffset.UtcNow
            )
        );
        var returned = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                original.Resource,
                original.Revision.Payload,
                original.Revision.MediaType,
                original.Revision.CaptureVersion,
                changed.Fetch.ResourceGeneration,
                DateTimeOffset.UtcNow
            )
        );
        returned.Revision.Uuid.Should().Be(original.Revision.Uuid);
        returned.Fetch.ResourceGeneration.Should().Be(original.Generation + 2);

        if (beforePreparation)
        {
            await AssertConflict(() => Prepare(source, SourceActorReviewAction.Accept, person));
        }
        else
        {
            await AssertConflict(() =>
                reviews.ApplyAsync(operation, prepared!.PreparationUuid, Reason, Operator)
            );
            (await store.GetPreparationAsync(prepared!.PreparationUuid))
                .Should()
                .BeEquivalentTo(prepared);
        }

        (await fixture.Work.GetSubjectAsync(sourceOwner.SubjectUuid))
            .Should()
            .BeEquivalentTo(sourceOwner);
        (await HistoryForClaim(source.Claim)).Should().BeEmpty();
        (await ReceiptCount([operation])).Should().Be(0);
    }

    [Test]
    public async Task RedirectAndItsLaterReversalBothInvalidateSavedTargetResolution()
    {
        var source = await Source();
        var original = await Person("Original identity " + Guid.NewGuid());
        var survivor = await Person("Surviving identity " + Guid.NewGuid());
        var beforeMerge = await Prepare(source, SourceActorReviewAction.Accept, original);
        var originalOwner = await fixture.Work.GetSubjectAsync(
            new NormalizationSubjectKey("people", "person", original.Uuid)
        );
        var merge = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                merge,
                originalOwner!.SubjectUuid,
                originalOwner.EditorialRevision,
                "merge",
                original.Uuid,
                await PersonRevision(original.Uuid),
                new(survivor.Uuid, "Surviving identity"),
                await PersonRevision(survivor.Uuid),
                [],
                Reason
            ),
            Operator
        );

        await AssertConflict(() =>
            reviews.ApplyAsync(Guid.NewGuid(), beforeMerge.PreparationUuid, Reason, Operator)
        );
        var redirected = await Prepare(source, SourceActorReviewAction.Accept, original);
        redirected.Target!.OriginalPersonUuid.Should().Be(original.Uuid);
        redirected.Target.ResolvedPersonUuid.Should().Be(survivor.Uuid);
        redirected
            .Target.Members.Select(member => member.PersonUuid)
            .Should()
            .BeEquivalentTo([original.Uuid, survivor.Uuid]);
        originalOwner = await fixture.Work.GetSubjectAsync(originalOwner.SubjectUuid);
        await people.ReverseMergeAsync(
            new(
                Guid.NewGuid(),
                originalOwner!.SubjectUuid,
                originalOwner.EditorialRevision,
                merge,
                original.Uuid,
                survivor.Uuid,
                await PersonRevision(original.Uuid),
                await PersonRevision(survivor.Uuid),
                Reason
            ),
            Operator
        );

        await AssertConflict(() =>
            reviews.ApplyAsync(Guid.NewGuid(), redirected.PreparationUuid, Reason, Operator)
        );
        (await HistoryForClaim(source.Claim)).Should().BeEmpty();
        var fresh = await Prepare(source, SourceActorReviewAction.Accept, original);
        fresh.Target!.ResolvedPersonUuid.Should().Be(original.Uuid);
        await reviews.ApplyAsync(Guid.NewGuid(), fresh.PreparationUuid, Reason, Operator);
        (await Decision(fresh.ReviewStateUuid)).SelectedPersonUuid.Should().Be(original.Uuid);
    }

    [Test]
    public async Task HistoricalRetractionKeepsOriginalEvidenceAfterItsFieldDisappears()
    {
        var source = await Source();
        var person = await Person("Historical accepted target " + Guid.NewGuid());
        var accepted = await Prepare(source, SourceActorReviewAction.Accept, person);
        await reviews.ApplyAsync(Guid.NewGuid(), accepted.PreparationUuid, Reason, Operator);
        var current = await AdvanceInput(accepted);
        var retract = await Prepare(source, SourceActorReviewAction.Retract);
        retract.Request.Claim.Should().Be(accepted.Request.Claim);
        retract.OriginalField.Should().BeEquivalentTo(accepted.OriginalField);
        retract.ReadSet.Inputs.Should().Contain(input => input.RevisionUuid == current);
        retract
            .Target.Should()
            .BeNull(
                "withdrawing a historical binding need not consume its former target's current identity"
            );

        await reviews.ApplyAsync(Guid.NewGuid(), retract.PreparationUuid, Reason, Operator);

        (await History(retract.ReviewStateUuid))
            .Select(row => row.State)
            .Should()
            .Equal("accepted", "retracted");
    }

    [Test]
    public async Task CancellationWhileWaitingForOwnerLocksLeavesNoDecisionAndTheSameOperationCanRetry()
    {
        var source = await Source();
        var prepared = await Prepare(source, SourceActorReviewAction.Reject);
        var operation = Guid.NewGuid();
        var before = await Owners(prepared);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT uuid FROM normalization.work_subjects WHERE uuid=@uuid FOR UPDATE",
            new { uuid = prepared.SourceOwner.SubjectUuid },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = reviews.ApplyAsync(
            operation,
            prepared.PreparationUuid,
            Reason,
            Operator,
            cancellation.Token
        );
        try
        {
            await WaitForBlocker(blocker.ProcessID);
            cancellation.Cancel();
            var exception = await ((Func<Task>)(() => pending)).Should().ThrowAsync<Exception>();
            IsCancellation(exception.Which).Should().BeTrue();
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
                // The original operation remains available because no review committed.
            }
        }
        (await ReceiptCount([operation])).Should().Be(0);
        (await HistoryForClaim(source.Claim)).Should().BeEmpty();
        (await Owners(prepared)).Should().BeEquivalentTo(before);
        (await store.GetPreparationAsync(prepared.PreparationUuid))
            .Should()
            .BeEquivalentTo(prepared);

        (await reviews.ApplyAsync(operation, prepared.PreparationUuid, Reason, Operator))
            .Replayed.Should()
            .BeFalse();
        (await reviews.ApplyAsync(operation, prepared.PreparationUuid, Reason, Operator))
            .Replayed.Should()
            .BeTrue();
        (await HistoryForClaim(source.Claim)).Should().ContainSingle();
    }

    [TestCase("update")]
    [TestCase("delete")]
    public async Task RetainedPreparationCannotBeChangedOrRemoved(string mutation)
    {
        var source = await Source();
        var prepared = await Prepare(source, SourceActorReviewAction.Reject);
        var sql =
            mutation == "update"
                ? "UPDATE normalization.source_actor_review_preparations SET preparation='{}'::jsonb WHERE preparation_uuid=@uuid"
                : "DELETE FROM normalization.source_actor_review_preparations WHERE preparation_uuid=@uuid";
        var mutate = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(sql, new { uuid = prepared.PreparationUuid })
            );

        var exception = await mutate.Should().ThrowAsync<Exception>();

        HasStorageRejection(exception.Which).Should().BeTrue();
        (await store.GetPreparationAsync(prepared.PreparationUuid))
            .Should()
            .BeEquivalentTo(prepared);
    }

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException
        || exception.InnerException != null && IsCancellation(exception.InnerException);

    private static bool HasStorageRejection(Exception exception) =>
        exception is PostgresException { SqlState: "P0001" }
        || exception.InnerException != null && HasStorageRejection(exception.InnerException);

    private static async Task WaitForBlocker(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await observer.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                        WHERE datname=current_database() AND @blockerPid=ANY(pg_blocking_pids(pid)))
                    """,
                    new { blockerPid }
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("Review must reach the shared Work owner lock before cancellation.");
    }
}
