using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase("person")]
    [TestCase("group")]
    public async Task QualifiedMachinePublicationUsesActualAcceptedAliasesWithoutChangingIdentityOrCredit(
        string kind
    )
    {
        await SelectPolicy(Policy());
        var name = Unique("Machine Robin");
        var alias = Unique("Machine Finch");
        var person = await Person(name, kind);
        var source = await Source(Personnel(name, alias));
        Claims(source).Single().Decision.PersonUuid.Should().Be(person);
        var unchanged = await NonAliasState();

        var applied = await Apply(source);
        applied.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var names = Receipts(applied).SelectMany(receipt => receipt.Names).ToArray();
        names.Select(outcome => outcome.Wording).Should().Contain([name, alias]);
        names
            .Should()
            .OnlyContain(outcome =>
                outcome.Disposition == SourceActorAliasNameDisposition.Accepted
                || outcome.Disposition == SourceActorAliasNameDisposition.ExactDuplicate
            );
        names
            .Should()
            .OnlyContain(outcome =>
                outcome.ActualAliasUuid != null
                && outcome.AssignmentUuid != null
                && outcome.SupportUuid != null
            );
        (await CurrentAliases(person)).Select(value => value.Wording).Should().Contain(alias);
        var accidentalUnmanaged = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM normalization.person_alias_unmanaged_support unmanaged
                JOIN normalization.person_alias_contributor_support support ON support.alias_uuid=unmanaged.alias_uuid
                JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                JOIN normalization.person_aliases alias ON alias.uuid=support.alias_uuid
                WHERE assignment.source_uuid=@sourceUuid AND alias.wording=@alias
                """,
                new { sourceUuid = source.Source.SourceUuid, alias }
            )
        );
        accidentalUnmanaged.Should().Be(0);
        (await NonAliasState()).Should().Be(unchanged);
        (await people.ReadCreditContextAsync(source.Source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task TrustedOutOfPoolSelectionPublishesOriginalIdentityWithoutAQualifiedPolicy()
    {
        var sourceName = Unique("Source asserted Robin");
        var alias = Unique("Source asserted Finch");
        var selected = await Person(Unique("Unrelated operator selected identity"));
        var source = await Source(Personnel(sourceName, alias));
        Claims(source).Single().Decision.PersonUuid.Should().BeNull();
        await Review(source, SourceActorReviewAction.Accept, selected);
        source = await Resolve(source.Source);
        var unchanged = await NonAliasState();

        var applied = await Apply(source);

        applied.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        applied
            .Stages.Where(stage => stage.Receipt != null)
            .Should()
            .OnlyContain(stage =>
                stage.OriginalPersonUuid == selected
                && stage.Facts.Policy == null
                && stage.Facts.CandidatePools.Count == 0
            );
        (await CurrentAliases(selected))
            .Select(value => value.Wording)
            .Should()
            .Contain([sourceName, alias]);
        (await NonAliasState()).Should().Be(unchanged);
    }

    [Test]
    public async Task MissingPolicyCannotTurnAProposalIntoAcceptedAuthority()
    {
        var name = Unique("No policy Robin");
        await Person(name);
        var source = await Source(Personnel(name, Unique("No policy Finch")));
        var before = await AcceptedAuthority();

        var result = await Apply(source);

        result
            .Result!.Disposition.Should()
            .BeOneOf(
                SourceActorAliasApplicationDisposition.Unresolved,
                SourceActorAliasApplicationDisposition.Review
            );
        Receipts(result).Should().BeEmpty();
        (await AcceptedAuthority()).Should().Be(before);
        (await ActiveAssignments(source.Source.SourceUuid)).Should().Be(0);
    }

    [Test]
    public async Task ExactReplayReturnsOriginalReceiptAfterLaterSourceAndPolicyChanges()
    {
        await SelectPolicy(Policy());
        var name = Unique("Replay Robin");
        await Person(name);
        var source = await Source(Personnel(name, Unique("Replay Finch")));
        var first = await Apply(source);
        var before = await AcceptedAuthority();
        await SetDescription(source.Source, "Later unrelated source prose");
        await SelectPolicy(null);

        var replay = await NewPublisher()
            .ApplyAsync(first.AttemptUuid, source.Resolution.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Plan.Should().BeEquivalentTo(first.Plan);
        replay.Stages.Should().BeEquivalentTo(first.Stages);
        replay.Result.CommittedStageUuids.Should().Equal(first.Result!.CommittedStageUuids);
        (await AcceptedAuthority()).Should().Be(before);
        var changed = await Resolve(source.Source);
        var conflict = () =>
            publisher.ApplyAsync(first.AttemptUuid, changed.Resolution.AttemptUuid);
        await conflict.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task TwoSourcesWithTheSameAliasWordingKeepIndependentCitationsWhenOneWithdraws()
    {
        await SelectPolicy(Policy());
        var name = Unique("Shared Robin");
        var alias = Unique("Shared Finch");
        var person = await Person(name);
        var first = await Source(Personnel(name, alias));
        var firstApplication = await Apply(first);
        var second = await Source(Personnel(name, alias));
        var secondApplication = await Apply(second);
        var firstAliases = Receipts(firstApplication)
            .SelectMany(value => value.Names)
            .Where(value => value.Wording == alias)
            .Select(value => value.ActualAliasUuid!.Value)
            .ToArray();
        var secondAliases = Receipts(secondApplication)
            .SelectMany(value => value.Names)
            .Where(value => value.Wording == alias)
            .Select(value => value.ActualAliasUuid!.Value)
            .ToArray();
        firstAliases.Should().NotBeEmpty();
        secondAliases
            .Should()
            .NotBeEmpty()
            .And.NotIntersectWith(
                firstAliases,
                "every distinct original source citation remains a distinct canonical assertion"
            );
        await Review(first, SourceActorReviewAction.Reject);
        first = await Resolve(first.Source);

        var withdrawal = await Apply(first);

        (await ActiveAssignments(first.Source.SourceUuid)).Should().Be(0);
        (await ActiveAssignments(second.Source.SourceUuid)).Should().BeGreaterThan(0);
        var currentAliases = (await CurrentAliases(person))
            .Where(value => value.Wording == alias)
            .Select(value => value.Uuid);
        currentAliases.Should().Contain(secondAliases).And.NotIntersectWith(firstAliases);
        Receipts(withdrawal)
            .SelectMany(value => value.RetiredAssignmentUuids)
            .Should()
            .NotBeEmpty();
        withdrawal
            .Stages.Where(value => value.Receipt != null)
            .Should()
            .OnlyContain(value => value.OriginalPersonUuid == person);
    }

    [Test]
    public async Task CompleteZeroAliasFieldClearsSupportAndLaterReactivationUsesNewAuthorityRows()
    {
        await SelectPolicy(Policy());
        var name = Unique("Clear Robin");
        var alias = Unique("Clear Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, alias));
        var first = await Apply(source);
        var firstOutcomes = Receipts(first)
            .SelectMany(value => value.Names)
            .Where(value => value.Wording == alias)
            .ToArray();
        firstOutcomes.Should().NotBeEmpty();
        source = await Replace(source, "Personnel:\nRecorded Robin - guitar");
        source
            .Resolution.Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.OriginalScope.Extraction.Scopes)
            .Should()
            .Contain(coverage => coverage.Complete);
        Claims(source).Should().BeEmpty();

        var cleared = await Apply(source);

        cleared.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        (await ActiveAssignments(source.Source.SourceUuid)).Should().Be(0);
        (await CurrentAliases(person)).Should().NotContain(value => value.Wording == alias);
        var clearMap = Receipts(cleared)
            .Last()
            .ResultingMap.Single(value => value.Scope.SourceUuid == source.Source.SourceUuid);
        clearMap.Bindings.Should().BeEmpty();
        clearMap.LastCompleteObservation.Should().NotBeNull();
        clearMap.LastCompleteObservation!.ExplicitClear.Should().BeTrue();
        clearMap
            .LastCompleteObservation.FieldJsonPointer.Should()
            .Be(
                clearMap.Scope.FieldJsonPointer,
                "complete coverage must identify this exact field within the original scope"
            );
        source = await Replace(source, Personnel(name, alias));
        source
            .Extraction.Ticket.Inputs.Select(input => input.RevisionUuid)
            .Should()
            .Contain(
                first
                    .Stages[0]
                    .Facts.OriginalExtractionTicket.Inputs.Select(input => input.RevisionUuid),
                "restoring the exact stable legacy snapshot reuses its immutable input revision"
            );
        var reactivated = await Apply(source);
        var nextOutcomes = Receipts(reactivated)
            .SelectMany(value => value.Names)
            .Where(value => value.Wording == alias)
            .ToArray();
        nextOutcomes
            .Select(value => value.ActualAliasUuid)
            .Should()
            .BeEquivalentTo(firstOutcomes.Select(value => value.ActualAliasUuid));
        foreach (var next in nextOutcomes)
        {
            var previous = firstOutcomes.Single(value =>
                value.ActualAliasUuid == next.ActualAliasUuid
            );
            next.AssignmentUuid.Should().NotBeNull();
            next.SupportUuid.Should().NotBeNull();
            previous.AssignmentUuid.Should().NotBeNull();
            previous.SupportUuid.Should().NotBeNull();
            next.AssignmentUuid.Value.Should().NotBe(previous.AssignmentUuid.Value);
            next.SupportUuid.Value.Should().NotBe(previous.SupportUuid.Value);
        }
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == alias);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task UnavailableOrSemanticallyUnknownEmptyFieldPreservesPreviouslyAcceptedSupport(
        string? replacement
    )
    {
        await SelectPolicy(Policy());
        var name = Unique("Incomplete Robin");
        var alias = Unique("Incomplete Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, alias));
        var initial = await Apply(source);
        var oldBindings = Receipts(initial)
            .Last()
            .ResultingMap.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.AssignmentUuid)
            .ToArray();
        source = await Replace(source, replacement);

        var result = await Apply(source);

        result.Result!.Disposition.Should().NotBe(SourceActorAliasApplicationDisposition.Complete);
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == alias);
        (await ActiveAssignments(source.Source.SourceUuid)).Should().BeGreaterThan(0);
        Receipts(result)
            .SelectMany(value => value.RetiredAssignmentUuids)
            .Should()
            .NotIntersectWith(oldBindings);
    }

    [Test]
    public async Task ExactHistoricalRejectionWithdrawsItsBindingAfterTheOriginalFieldDisappears()
    {
        await SelectPolicy(Policy());
        var name = Unique("Historical withdrawal Robin");
        var alias = Unique("Historical withdrawal Finch");
        var person = await Person(name);
        var historical = await Source(Personnel(name, alias));
        var accepted = await Apply(historical);
        var oldAssignment = Receipts(accepted)
            .SelectMany(receipt => receipt.Names)
            .Where(outcome => outcome.Wording == alias)
            .Select(outcome => outcome.AssignmentUuid!.Value)
            .Distinct()
            .Single();
        var current = await Replace(historical, null);
        await Review(historical, SourceActorReviewAction.Reject);
        current = await Resolve(current.Source);

        var withdrawn = await Apply(current);

        (await ActiveAssignments(current.Source.SourceUuid)).Should().Be(0);
        (await CurrentAliases(person)).Should().NotContain(value => value.Wording == alias);
        Receipts(withdrawn)
            .SelectMany(receipt => receipt.RetiredAssignmentUuids)
            .Should()
            .Contain(oldAssignment);
        withdrawn
            .Stages.Where(stage => stage.Receipt != null)
            .Should()
            .OnlyContain(stage => stage.OriginalPersonUuid == person);
        var observedInputs = withdrawn
            .Stages.SelectMany(stage => stage.Facts.CurrentReadSet.Inputs ?? [])
            .ToArray();
        observedInputs
            .Should()
            .Contain(
                current.Extraction.Ticket.Inputs,
                "historical rejection must still fence the actual current heads"
            );
        observedInputs
            .Should()
            .NotContain(
                input =>
                    input.RevisionUuid == historical.Extraction.Ticket.Inputs.Single().RevisionUuid,
                "historical negative authority remains separate from today's current-head observations"
            );
    }

    [Test]
    public async Task CanonicalVetoMakesReplacementPartialAndKeepsEverySupersededScopeBinding()
    {
        await SelectPolicy(Policy());
        var name = Unique("Protected Robin");
        var oldAlias = Unique("Old Finch");
        var blockedAlias = Unique("Blocked Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var initial = await Apply(source);
        var oldAssignments = Receipts(initial)
            .Last()
            .ResultingMap.SelectMany(value => value.Bindings)
            .Select(value => value.AssignmentUuid)
            .ToArray();
        await ReviewCanonicalAlias(person, blockedAlias, "retract");
        source = await Replace(source, Personnel(name, blockedAlias));
        await Review(source, SourceActorReviewAction.Accept, person);
        source = await Resolve(source.Source);

        var partial = await Apply(source);

        partial
            .Result!.Disposition.Should()
            .BeOneOf(
                SourceActorAliasApplicationDisposition.Partial,
                SourceActorAliasApplicationDisposition.Review
            );
        Receipts(partial)
            .SelectMany(value => value.Names)
            .Should()
            .Contain(value =>
                value.Wording == blockedAlias
                && (
                    value.Disposition
                        == SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim
                    || value.Disposition == SourceActorAliasNameDisposition.Pending
                )
                && value.SupportUuid == null
            );
        Receipts(partial)
            .SelectMany(value => value.RetiredAssignmentUuids)
            .Should()
            .NotIntersectWith(oldAssignments);
        (await CurrentAliases(person))
            .Select(value => value.Wording)
            .Should()
            .Contain(oldAlias)
            .And.NotContain(blockedAlias);
    }

    [Test]
    public async Task TrustedReassignmentKeepsCommittedWithdrawalWhenNewAliasIsCanonicallyProtected()
    {
        var oldPerson = await Person(Unique("Old original target"));
        var newPerson = await Person(Unique("New original target"));
        var sourceName = Unique("Reassign Robin");
        var alias = Unique("Reassign Finch");
        var source = await Source(Personnel(sourceName, alias));
        await Review(source, SourceActorReviewAction.Accept, oldPerson);
        source = await Resolve(source.Source);
        await Apply(source);
        await ReviewCanonicalAlias(newPerson, alias, "retract");
        await Review(source, SourceActorReviewAction.Reassign, newPerson);
        source = await Resolve(source.Source);

        var result = await Apply(source);

        result
            .Result!.Disposition.Should()
            .BeOneOf(
                SourceActorAliasApplicationDisposition.Partial,
                SourceActorAliasApplicationDisposition.Review
            );
        Receipts(result)
            .Should()
            .Contain(receipt =>
                receipt.OriginalPersonUuid == oldPerson && receipt.RetiredAssignmentUuids.Count > 0
            );
        Receipts(result)
            .Should()
            .Contain(receipt =>
                receipt.OriginalPersonUuid == newPerson
                && receipt.Names.Any(value =>
                    value.Wording == alias
                    && (
                        value.Disposition
                            == SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim
                        || value.Disposition == SourceActorAliasNameDisposition.Pending
                    )
                )
            );
        (await CurrentAliases(oldPerson)).Should().NotContain(value => value.Wording == alias);
        (await CurrentAliases(newPerson)).Should().NotContain(value => value.Wording == alias);
        var replay = await NewPublisher()
            .ApplyAsync(result.AttemptUuid, source.Resolution.AttemptUuid);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Stages.Should().BeEquivalentTo(result.Stages);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SourcePublicationHonorsCanonicalNaturalClaimVetoAcrossEitherRedirectEnd(
        bool reviewRetiredEnd
    )
    {
        var originalName = Unique("Original component member");
        var survivorName = Unique("Surviving component member");
        var original = await Person(originalName);
        var survivor = await Person(survivorName);
        var blocked = Unique("Component protected Finch");
        await ReviewCanonicalAlias(reviewRetiredEnd ? original : survivor, blocked, "retract");
        var originalOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original)
            )
        )!;
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                originalOwner.SubjectUuid,
                originalOwner.EditorialRevision,
                "merge",
                original,
                await PersonRevision(original),
                new(survivor, survivorName),
                await PersonRevision(survivor),
                [],
                "These two existing original identities describe the same person"
            ),
            Operator
        );
        // Select the other original end explicitly. Redirects cannot route around a review.
        var selected = reviewRetiredEnd ? survivor : original;
        var source = await Source(Personnel(Unique("Component source Robin"), blocked));
        await Review(source, SourceActorReviewAction.Accept, selected);
        source = await Resolve(source.Source);

        var result = await Apply(source);

        result
            .Result!.Disposition.Should()
            .BeOneOf(
                SourceActorAliasApplicationDisposition.Partial,
                SourceActorAliasApplicationDisposition.Review
            );
        Receipts(result).Should().OnlyContain(receipt => receipt.OriginalPersonUuid == selected);
        Receipts(result)
            .SelectMany(receipt => receipt.Names)
            .Should()
            .Contain(outcome =>
                outcome.Wording == blocked
                && (
                    outcome.Disposition
                        == SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim
                    || outcome.Disposition == SourceActorAliasNameDisposition.Pending
                )
                && outcome.SupportUuid == null
            );
        (await CurrentAliases(original)).Should().NotContain(alias => alias.Wording == blocked);
        (await CurrentAliases(survivor)).Should().NotContain(alias => alias.Wording == blocked);
    }
}
