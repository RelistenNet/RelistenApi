using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task CancellationBetweenReassignmentStagesRetainsWithdrawalAndResumesOnlyTheSavedReplacement()
    {
        var oldPerson = await Person(Unique("Interrupted old original"));
        var newPerson = await Person(Unique("Interrupted new original"));
        var sourceName = Unique("Interrupted source Robin");
        var alias = Unique("Interrupted source Finch");
        var source = await Source(Personnel(sourceName, alias));
        await Review(source, SourceActorReviewAction.Accept, oldPerson);
        source = await Resolve(source.Source);
        await Apply(source);
        await Review(source, SourceActorReviewAction.Reassign, newPerson);
        source = await Resolve(source.Source);
        var applicationUuid = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:configuration:people:person:{newPerson}" },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = publisher.ApplyAsync(
            applicationUuid,
            source.Resolution.AttemptUuid,
            cancellation.Token
        );
        ExpandedApplication saved;
        try
        {
            saved = await WaitForWithdrawnAndPreparedReplacement(
                applicationUuid,
                oldPerson,
                newPerson
            );
            (await CurrentAliases(oldPerson)).Should().NotContain(value => value.Wording == alias);
            (await ActiveAssignments(source.Source.SourceUuid))
                .Should()
                .Be(
                    0,
                    "the old withdrawal commits before the blocked new original owner is admitted"
                );
            var replacement = saved.Stages.Single(stage =>
                stage.OriginalPersonUuid == newPerson && stage.Receipt == null
            );
            replacement.Ticket.Should().BeNull();
            cancellation.Cancel();
            var cancelled = () => pending;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Committed withdrawal remains accepted history; only the new stage resumes.
            }
        }
        var oldReceipt = saved
            .Stages.Single(stage => stage.OriginalPersonUuid == oldPerson)
            .Receipt!;
        var preparedReplacement = saved.Stages.Single(stage =>
            stage.OriginalPersonUuid == newPerson && stage.Receipt == null
        );

        var resumed = await NewPublisher()
            .ApplyAsync(applicationUuid, source.Resolution.AttemptUuid);

        resumed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        resumed
            .Stages.Single(stage => stage.StageUuid == oldReceipt.StageUuid)
            .Receipt.Should()
            .BeEquivalentTo(oldReceipt);
        var committedReplacement = resumed.Stages.Single(stage =>
            stage.StageUuid == preparedReplacement.StageUuid
        );
        committedReplacement.WorkUuid.Should().Be(preparedReplacement.WorkUuid);
        committedReplacement.Facts.Should().BeEquivalentTo(preparedReplacement.Facts);
        committedReplacement.Receipt.Should().NotBeNull();
        (await CurrentAliases(oldPerson)).Should().NotContain(value => value.Wording == alias);
        (await CurrentAliases(newPerson)).Should().Contain(value => value.Wording == alias);
        Receipts(resumed)
            .SelectMany(receipt => receipt.Names)
            .Should()
            .OnlyContain(outcome =>
                outcome.Disposition == SourceActorAliasNameDisposition.Accepted
                || outcome.Disposition == SourceActorAliasNameDisposition.ExactDuplicate
            );
    }

    private async Task<ExpandedApplication> WaitForWithdrawnAndPreparedReplacement(
        Guid applicationUuid,
        Guid oldPerson,
        Guid newPerson
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var current = await ReadExpandedApplication(applicationUuid);
            if (
                current != null
                && current.Stages.Any(stage =>
                    stage.OriginalPersonUuid == oldPerson
                    && stage.Receipt is { RetiredAssignmentUuids.Count: > 0 }
                )
                && current.Stages.Any(stage =>
                    stage.OriginalPersonUuid == newPerson && stage.Receipt == null
                )
            )
            {
                return current;
            }
            await Task.Delay(10);
        }
        throw new AssertionException(
            "Public Apply must retain the old withdrawal receipt before preparing the new original person's admission."
        );
    }
}
