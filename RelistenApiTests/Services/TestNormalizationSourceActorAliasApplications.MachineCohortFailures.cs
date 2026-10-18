using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SourceWideReviewAbsenceDefersUnrelatedOldMapFailureUntilAfterIndependentAddition(
        bool acceptedReviewOnRemovedField
    )
    {
        await SelectPolicy(Policy());
        var previous = await CohortActor();
        var incoming = await CohortActor();
        var recording = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE sources SET description=NULL,taper=NULL,transferrer=NULL,lineage=NULL,source=NULL,
                    taper_notes=@notes WHERE id=@SourceId
                """,
                new { recording.SourceId, notes = CohortPersonnel(previous) }
            )
        );
        var original = await Resolve(recording);
        if (acceptedReviewOnRemovedField)
        {
            await Review(original, SourceActorReviewAction.Accept, previous.Person);
            original = await Resolve(recording);
        }
        var accepted = await Apply(original);
        accepted.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var previousSupports = Receipts(accepted)
            .SelectMany(receipt => receipt.Names)
            .Where(name => name.SupportUuid.HasValue)
            .Select(name => name.SupportUuid!.Value)
            .Distinct()
            .ToArray();
        var originalStage = accepted.Stages.Last(stage => stage.Receipt != null);
        var source = await Replace(original, CohortPersonnel(incoming));
        Claims(source)
            .Should()
            .ContainSingle()
            .Which.Decision.PersonUuid.Should()
            .Be(incoming.Person);
        source
            .Resolution.Preparation!.SourceReviews.Reviews.Should()
            .BeEmpty(
                "the accepted historical review belongs to removed taper notes, outside today's description field"
            );
        var applicationUuid = Guid.NewGuid();
        var originalHash = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT request_hash FROM normalization.work_runs WHERE uuid=@WorkUuid",
                new { originalStage.WorkUuid }
            )
        );
        ExpandedApplication result;
        try
        {
            // This scoped fault changes only the actual historical admission hash. Accepted
            // rows and receipts remain genuine, and finally restores the exact original bytes.
            // A no-review preparation may defer this unrelated map until finalization; any
            // historical source review requires the complete withdrawal scan before additions.
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.work_runs SET request_hash=repeat('0',64) WHERE uuid=@WorkUuid",
                    new { originalStage.WorkUuid }
                )
            );

            result = await NewPublisher()
                .ApplyAsync(applicationUuid, source.Resolution.AttemptUuid);
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.work_runs SET request_hash=@originalHash WHERE uuid=@WorkUuid",
                    new { originalStage.WorkUuid, originalHash }
                )
            );
        }

        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        var committed = result.Stages.Where(stage => stage.Receipt != null).ToArray();
        if (acceptedReviewOnRemovedField)
        {
            committed.Should().BeEmpty();
            (await CurrentAliases(incoming.Person))
                .Should()
                .NotContain(alias => alias.Wording == incoming.Alias);
        }
        else
        {
            committed.Should().NotBeEmpty();
            committed.Should().OnlyContain(stage => stage.OriginalPersonUuid == incoming.Person);
            committed
                .Should()
                .Contain(stage =>
                    stage.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
                );
            (await CurrentAliases(incoming.Person))
                .Should()
                .Contain(alias => alias.Wording == incoming.Alias);
        }
        (await CurrentAliases(previous.Person))
            .Should()
            .Contain(alias => alias.Wording == previous.Alias);
        Receipts(result)
            .SelectMany(receipt => receipt.RetiredSupportUuids)
            .Should()
            .NotIntersectWith(previousSupports);
        (await applications.GetStageAsync(originalStage.StageUuid))
            .Should()
            .BeEquivalentTo(
                originalStage,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
    }
}
