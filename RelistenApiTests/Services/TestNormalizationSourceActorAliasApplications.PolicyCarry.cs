using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task DeactivationAfterCommittedNamesAllowsFinalizationWithoutRenewingTheirMachineAuthority()
    {
        await SelectPolicy(Policy());
        var name = Unique("Committed policy Robin");
        var oldAlias = Unique("Committed previous Finch");
        var newAlias = Unique("Committed replacement Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var previous = await Apply(source);
        var oldSupport = Receipts(previous)
            .Last()
            .ResultingMap.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.SupportUuid)
            .ToArray();
        source = await Replace(source, Personnel(name, newAlias));
        var prepared = await PrepareAdmittedSourceStage(source, person);
        var addition = prepared.Stages.Single();
        (await people.PublishSourceActorAliasStageAsync(prepared.AttemptUuid, addition.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var committed = (await applications.GetStageAsync(addition.StageUuid))!;
        committed.Facts.Policy.Should().NotBeNull();
        committed.Facts.CandidatePools.Should().NotBeEmpty();
        committed
            .Receipt!.Names.Should()
            .OnlyContain(value =>
                value.Disposition == SourceActorAliasNameDisposition.Accepted
                || value.Disposition == SourceActorAliasNameDisposition.ExactDuplicate
            );
        await SelectPolicy(null);

        var completed = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var retained = completed.Stages.Single(stage => stage.StageUuid == committed.StageUuid);
        retained.Facts.Should().BeEquivalentTo(committed.Facts);
        retained.Receipt.Should().BeEquivalentTo(committed.Receipt);
        var finalization = completed.Stages.Single(stage =>
            stage.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.FinalizeReplacement)
        );
        finalization.Facts.Policy.Should().BeNull();
        finalization.Facts.CandidatePools.Should().BeEmpty();
        finalization
            .Facts.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.Claims)
            .Should()
            .BeEmpty();
        finalization
            .Facts.CarriedClaims.Should()
            .ContainSingle(value =>
                value.StageUuid == committed.StageUuid && value.OriginalPersonUuid == person
            );
        finalization.Receipt!.RetiredSupportUuids.Should().Contain(oldSupport);
        (await CurrentAliases(person))
            .Should()
            .Contain(value => value.Wording == newAlias)
            .And.NotContain(value => value.Wording == oldAlias);
    }

    [Test]
    public async Task DeactivationMidApplicationPreservesCommittedNamesButHoldsEveryRemainingMachineClaim()
    {
        await SelectPolicy(Policy());
        var names = new[] { IndependentActorName(), IndependentActorName() };
        var aliases = new[] { IndependentActorName(), IndependentActorName() };
        var persons = new[] { await Person(names[0]), await Person(names[1]) };
        var source = await Source(
            "Personnel:\n"
                + string.Join(
                    "\n",
                    Enumerable
                        .Range(0, 2)
                        .Select(index => $"{names[index]} aka {aliases[index]} - vocals")
                )
        );
        Claims(source)
            .Should()
            .HaveCount(2)
            .And.OnlyContain(claim =>
                claim.Decision.Action == SourceActorResolutionAction.MatchExisting
            );
        var firstPerson = Claims(source).First().Decision.PersonUuid!.Value;
        var first = Array.IndexOf(persons, firstPerson);
        var remaining = 1 - first;
        var prepared = await PrepareAdmittedSourceStage(source, firstPerson);
        var stage = prepared.Stages.Single();
        (await people.PublishSourceActorAliasStageAsync(prepared.AttemptUuid, stage.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var committed = (await applications.GetStageAsync(stage.StageUuid))!;
        await SelectPolicy(null);

        var held = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        held.Result!.Disposition.Should().NotBe(SourceActorAliasApplicationDisposition.Complete);
        held.Stages.Single(value => value.StageUuid == committed.StageUuid)
            .Receipt.Should()
            .BeEquivalentTo(committed.Receipt);
        held.Stages.Where(value => value.Receipt != null)
            .Should()
            .OnlyContain(value => value.OriginalPersonUuid == firstPerson);
        (await CurrentAliases(firstPerson))
            .Should()
            .Contain(value => value.Wording == aliases[first]);
        (await CurrentAliases(persons[remaining]))
            .Should()
            .NotContain(value => value.Wording == aliases[remaining]);
        Receipts(held).SelectMany(receipt => receipt.RetiredSupportUuids).Should().BeEmpty();
    }
}
