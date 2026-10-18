using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task UnrelatedPeopleChangesFenceOnlyStagesThatActuallyConsumedAMachinePool(
        bool machine
    )
    {
        var canonicalName = IndependentActorName();
        var selected = await Person(canonicalName);
        var sourceName = machine ? canonicalName : IndependentActorName();
        var alias = IndependentActorName();
        if (machine)
        {
            await SelectPolicy(Policy());
        }
        var source = await Source(Personnel(sourceName, alias));
        if (!machine)
        {
            await Review(source, SourceActorReviewAction.Accept, selected);
            source = await Resolve(source.Source);
        }
        var saved = await PrepareAdmittedSourceStage(source, selected);
        var stage = saved.Stages.Single();
        stage
            .Facts.CurrentReadSet.CandidateUniverses!.Any(value =>
                value.Universe == NormalizationCandidateUniverseKey.People
            )
            .Should()
            .Be(machine);
        await Person(IndependentActorName());

        var result = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, source.Resolution.AttemptUuid);

        result
            .Stages.Single(value => value.StageUuid == stage.StageUuid)
            .Facts.Should()
            .BeEquivalentTo(stage.Facts);
        if (machine)
        {
            result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
            Receipts(result).Should().BeEmpty();
            (await CurrentAliases(selected)).Should().NotContain(value => value.Wording == alias);
        }
        else
        {
            result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
            (await CurrentAliases(selected)).Should().Contain(value => value.Wording == alias);
        }
    }

    [Test]
    public async Task ReceiptOnlyFinalizationDropsItsOwnOldPoolAndSurvivesUnrelatedPeopleChanges()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var saved = await PrepareAdmittedSourceStage(source, person);
        var addition = saved.Stages.Single();
        (await people.PublishSourceActorAliasStageAsync(saved.AttemptUuid, addition.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var committed = (await applications.GetStageAsync(addition.StageUuid))!;
        var finalization = await PauseSavedFinalization(
            saved.AttemptUuid,
            source.Resolution.AttemptUuid,
            person
        );
        finalization
            .Deltas.Should()
            .OnlyContain(value => value.Kind == SourceActorAliasDeltaKind.FinalizeReplacement);
        finalization
            .Facts.CarriedClaims.Should()
            .ContainSingle(value => value.StageUuid == committed.StageUuid);
        finalization.Facts.Policy.Should().BeNull();
        finalization.Facts.CandidatePools.Should().BeEmpty();
        finalization
            .Facts.CurrentReadSet.CandidateUniverses.Should()
            .NotContain(
                value => value.Universe == NormalizationCandidateUniverseKey.People,
                "the prior own source contributor's old pool is historical acceptance, not a current read"
            );
        await Person(IndependentActorName());

        var completed = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        completed
            .Stages.Single(value => value.StageUuid == committed.StageUuid)
            .Receipt.Should()
            .BeEquivalentTo(committed.Receipt);
        completed
            .Stages.Single(value => value.StageUuid == finalization.StageUuid)
            .Facts.Should()
            .BeEquivalentTo(finalization.Facts);
    }

    private async Task<SourceActorAliasPersonStage> PauseSavedFinalization(
        Guid applicationUuid,
        Guid resolutionUuid,
        Guid person
    )
    {
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:configuration:people:person:{person}" },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = NewPublisher()
            .ApplyAsync(applicationUuid, resolutionUuid, cancellation.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var current = await ReadExpandedApplication(applicationUuid);
                var stage = current?.Stages.SingleOrDefault(value =>
                    value.Receipt == null && value.TerminalReason == null
                );
                if (stage != null)
                {
                    stage.Ticket.Should().BeNull();
                    cancellation.Cancel();
                    var cancelled = () => pending;
                    await cancelled.Should().ThrowAsync<OperationCanceledException>();
                    return stage;
                }
                await Task.Delay(10);
            }
            throw new AssertionException(
                "Apply must save the receipt-only finalization before its blocked admission."
            );
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
                // Cancellation preserves this exact preparation for public Apply recovery.
            }
        }
    }
}
