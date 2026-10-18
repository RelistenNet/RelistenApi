using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task HistoricalSourceAfterSafeAdditionReportsStaleAndRetainsEveryCommittedReceipt()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var oldAlias = IndependentActorName();
        var newAlias = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var old = await Apply(source);
        var oldSupport = Receipts(old)
            .Last()
            .ResultingMap.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.SupportUuid)
            .ToArray();
        source = await Replace(source, Personnel(name, newAlias));
        var prepared = await PrepareAdmittedSourceStage(source, person);
        var stage = prepared.Stages.Single();
        (await people.PublishSourceActorAliasStageAsync(prepared.AttemptUuid, stage.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var committed = (await applications.GetStageAsync(stage.StageUuid))!;
        await Replace(source, "The original complete performer field has now changed.");

        var stale = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        stale.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        stale.Result.Reason.Should().Be("source_proof_is_historical");
        stale.Stages.Should().ContainSingle();
        stale.Stages.Single().Receipt.Should().BeEquivalentTo(committed.Receipt);
        stale.Stages.Single().Facts.Should().BeEquivalentTo(committed.Facts);
        Receipts(stale)
            .SelectMany(receipt => receipt.RetiredSupportUuids)
            .Should()
            .NotIntersectWith(oldSupport);
        (await CurrentAliases(person))
            .Select(alias => alias.Wording)
            .Should()
            .Contain([oldAlias, newAlias]);
    }

    [Test]
    public async Task HistoricalSourceStillAppliesAnAuthenticatedNegativeBeforeReportingItsScopeStale()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var alias = IndependentActorName();
        var person = await Person(name);
        var historical = await Source(Personnel(name, alias));
        var accepted = await Apply(historical);
        var originalAssignments = Receipts(accepted)
            .Last()
            .ResultingMap.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.AssignmentUuid)
            .Distinct()
            .ToArray();
        await Replace(historical, null);
        await Review(historical, SourceActorReviewAction.Reject);

        // The exact old proposal is deliberately historical. A current authenticated human
        // negative can withdraw its old binding, but cannot certify today's whole scope.
        var withdrawn = await Apply(historical);

        withdrawn.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        withdrawn.Result.Reason.Should().Be("source_proof_is_historical");
        Receipts(withdrawn)
            .SelectMany(receipt => receipt.RetiredAssignmentUuids)
            .Should()
            .Contain(originalAssignments);
        withdrawn
            .Stages.Should()
            .OnlyContain(stage =>
                stage.Deltas.All(delta => delta.Kind == SourceActorAliasDeltaKind.Retire)
            );
        (await ActiveAssignments(historical.Source.SourceUuid)).Should().Be(0);
        (await CurrentAliases(person)).Should().NotContain(value => value.Wording == alias);
    }
}
