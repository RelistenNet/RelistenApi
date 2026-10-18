using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task FirstMachineCohortKeepsCompleteOriginalEvidenceAndDefersTheOtherPersonsPool()
    {
        await SelectPolicy(Policy());
        var first = await CohortActor();
        var second = await CohortActor();
        var source = await Source(CohortPersonnel(first, second));
        Claims(source)
            .Select(claim => claim.Decision.PersonUuid)
            .Should()
            .Equal(first.Person, second.Person);
        var prepared = await PrepareAdmittedSourceStage(source, first.Person);
        var stage = prepared.Stages.Single();

        stage.Facts.CandidatePools.Should().ContainSingle();
        AssertCompleteCohortEvidence(stage, source);
        var claims = CohortStageClaims(stage).ToArray();
        claims[0].CandidatePoolIndex.Should().Be(0);
        claims[0].Decision.PersonUuid.Should().Be(first.Person);
        claims[1].CandidatePoolIndex.Should().BeNull();
        claims[1].Comparisons.Should().BeEmpty();
        claims[1].Decision.PersonUuid.Should().BeNull();
        stage
            .Facts.Targets.Select(target => target.OriginalPersonUuid)
            .Should()
            .NotContain(second.Person);
        stage.Deltas.Should().OnlyContain(delta => delta.Kind == SourceActorAliasDeltaKind.Add);

        var completed = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var retained = completed.Stages.Single(value => value.StageUuid == stage.StageUuid);
        retained
            .Facts.Should()
            .BeEquivalentTo(
                stage.Facts,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        retained
            .Ticket.Should()
            .BeEquivalentTo(
                stage.Ticket,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        retained.Receipt.Should().NotBeNull();
        (await CurrentAliases(first.Person))
            .Should()
            .Contain(alias => alias.Wording == first.Alias);
        (await CurrentAliases(second.Person))
            .Should()
            .Contain(alias => alias.Wording == second.Alias);
    }

    [Test]
    public async Task NonadjacentClaimsWithTheSameOriginalPersonShareOneAdditionStage()
    {
        await SelectPolicy(Policy());
        var first = await CohortActor();
        var middle = await CohortActor();
        var repeated = first with { Alias = IndependentActorName() };
        var source = await Source(CohortPersonnel(first, middle, repeated));
        Claims(source)
            .Select(claim => claim.Decision.PersonUuid)
            .Should()
            .Equal(first.Person, middle.Person, first.Person);
        var prepared = await PrepareAdmittedSourceStage(source, first.Person);
        var stage = prepared.Stages.Single();

        AssertCompleteCohortEvidence(stage, source);
        stage.Facts.CandidatePools.Should().HaveCount(2);
        var additions = stage
            .Deltas.Where(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            .ToArray();
        additions.Should().HaveCount(2);
        additions
            .Select(delta => delta.Coordinates!.AssertionUuid)
            .Should()
            .BeEquivalentTo(
                Claims(source)
                    .Where(claim => claim.Decision.PersonUuid == first.Person)
                    .Select(claim => claim.Coordinates.AssertionUuid)
            );
        var deferred = CohortStageClaims(stage)
            .Single(claim =>
                claim.Coordinates.AssertionUuid
                == Claims(source).ElementAt(1).Coordinates.AssertionUuid
            );
        deferred.CandidatePoolIndex.Should().BeNull();
        deferred.Decision.PersonUuid.Should().BeNull();

        var completed = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        completed
            .Stages.Where(value =>
                value.OriginalPersonUuid == first.Person
                && value.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            )
            .Should()
            .ContainSingle();
        (await CurrentAliases(first.Person))
            .Select(alias => alias.Wording)
            .Should()
            .Contain([first.Alias, repeated.Alias]);
        (await CurrentAliases(middle.Person))
            .Should()
            .Contain(alias => alias.Wording == middle.Alias);
    }

    [Test]
    public async Task AnAmbiguousOriginalHintDoesNotBlockAClearLaterPersonOrClearTheHeldField()
    {
        await SelectPolicy(Policy());
        var held = await CohortActor();
        var clear = await CohortActor();
        var original = await Source(CohortPersonnel(held));
        var previous = await Apply(original);
        var oldSupports = Receipts(previous)
            .SelectMany(receipt => receipt.Names)
            .Where(name => name.SupportUuid.HasValue)
            .Select(name => name.SupportUuid!.Value)
            .Distinct()
            .ToArray();
        var replacement = held with { Alias = IndependentActorName() };
        var source = await Replace(original, CohortPersonnel(replacement, clear));
        Claims(source)
            .Select(claim => claim.Decision.PersonUuid)
            .Should()
            .Equal(held.Person, clear.Person);
        var namesake = await Person(held.Name);
        var prepared = await PrepareAdmittedSourceStage(source, clear.Person);
        var stage = prepared.Stages.Single();
        stage.Facts.CandidatePools.Should().HaveCount(2);
        var firstClaim = CohortStageClaims(stage).First();
        firstClaim.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        firstClaim.Decision.PersonUuid.Should().BeNull();
        firstClaim.CandidatePoolIndex.Should().NotBeNull();
        stage
            .Facts.CandidatePools[firstClaim.CandidatePoolIndex!.Value]
            .Facts.Select(fact => fact.Hit.Uuid)
            .Should()
            .Contain([held.Person, namesake]);

        var completed = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Review);
        completed
            .Stages.Where(value => value.Receipt != null)
            .Should()
            .NotContain(value =>
                value.OriginalPersonUuid == held.Person
                && value.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            );
        (await CurrentAliases(held.Person))
            .Should()
            .Contain(alias => alias.Wording == held.Alias)
            .And.NotContain(alias => alias.Wording == replacement.Alias);
        (await CurrentAliases(clear.Person))
            .Should()
            .Contain(alias => alias.Wording == clear.Alias);
        Receipts(completed)
            .SelectMany(receipt => receipt.RetiredSupportUuids)
            .Should()
            .NotIntersectWith(oldSupports);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReceiptOnlyChangesFenceConsumedNegativeOwnersButLeaveDeferredOwnersIndependent(
        bool negativePrefix
    )
    {
        await SelectPolicy(Policy());
        var first = await CohortActor();
        var second = await CohortActor();
        var source = await Source(CohortPersonnel(first, second));
        var changedPerson = negativePrefix ? await Person(first.Name) : second.Person;
        var selected = negativePrefix ? second.Person : first.Person;
        var prepared = await PrepareAdmittedSourceStage(source, selected);
        var stage = prepared.Stages.Single();
        stage.Facts.CandidatePools.Should().HaveCount(negativePrefix ? 2 : 1);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", changedPerson)
            )
        )!;
        stage
            .Facts.CurrentReadSet.OwnerObservations!.Any(value =>
                value.SubjectUuid == owner.SubjectUuid
            )
            .Should()
            .Be(negativePrefix);
        var accepted = await AcceptedAuthority();
        var unchangedOwners = await ReadOtherCohortOwners(stage, owner.SubjectUuid);
        await AdvanceCohortOwnerReceiptOnly(changedPerson);
        (await ReadOtherCohortOwners(stage, owner.SubjectUuid))
            .Should()
            .BeEquivalentTo(
                unchangedOwners,
                "a receipt-only isolation case requires every other consumed owner, including source and extraction, to remain unchanged"
            );

        var publication = await people.PublishSourceActorAliasStageAsync(
            prepared.AttemptUuid,
            stage.StageUuid
        );

        publication
            .Status.Should()
            .Be(
                negativePrefix
                    ? NormalizationPublicationStatus.Stale
                    : NormalizationPublicationStatus.Published
            );
        var retained = (await applications.GetStageAsync(stage.StageUuid))!;
        retained
            .Facts.Should()
            .BeEquivalentTo(
                stage.Facts,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        retained
            .Ticket.Should()
            .BeEquivalentTo(
                stage.Ticket,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        if (negativePrefix)
        {
            retained.Receipt.Should().BeNull();
            (await AcceptedAuthority()).Should().Be(accepted);
        }
        else
        {
            retained.Receipt.Should().NotBeNull();
            var completed = await NewPublisher()
                .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);
            completed
                .Result!.Disposition.Should()
                .Be(SourceActorAliasApplicationDisposition.Complete);
        }
    }

    [Test]
    public async Task FreshWinnersInsideOneOriginalHintGroupRemainFencedAndAreRefreshedAfterTheSelectedPersonPublishes()
    {
        await SelectPolicy(Policy());
        var first = await CohortActor();
        var otherName = IndependentActorName();
        var input = await fixture.Capture(otherName, "people");
        await fixture.PublishPeople(
            people,
            input,
            new([], [new(Guid.NewGuid(), first.Person, otherName, input.Reference)], [], [])
        );
        var otherClaim = new CohortActorFixture(first.Person, otherName, IndependentActorName());
        var source = await Source(CohortPersonnel(first, otherClaim));
        Claims(source)
            .Select(claim => claim.Decision.PersonUuid)
            .Should()
            .Equal(first.Person, first.Person);
        await ReviewCanonicalAlias(first.Person, otherName, "retract");
        var newWinner = await Person(otherName);
        var prepared = await PrepareAdmittedSourceStage(source, first.Person);
        var stage = prepared.Stages.Single();
        var claims = CohortStageClaims(stage).ToArray();

        claims.Select(claim => claim.Decision.PersonUuid).Should().Equal(first.Person, newWinner);
        stage.Facts.CandidatePools.Should().HaveCount(2);
        stage.Facts.Targets.Select(target => target.OriginalPersonUuid).Should().Contain(newWinner);
        stage
            .Deltas.Where(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            .Should()
            .ContainSingle();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", newWinner)
            )
        )!;
        stage
            .Facts.CurrentReadSet.OwnerObservations.Should()
            .ContainEquivalentOf(
                new NormalizationSubjectObservation(
                    owner.SubjectUuid,
                    owner.DesiredGeneration,
                    owner.EditorialRevision,
                    owner.PublishedWorkUuid
                )
            );
        var originalEpoch = stage
            .Facts.CandidatePools.SelectMany(pool => pool.ReadSet.CandidateUniverses)
            .Where(value => value.Universe == NormalizationCandidateUniverseKey.People)
            .Select(value => value.Generation)
            .Distinct()
            .Single();

        var completed = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var next = completed.Stages.Single(value =>
            value.OriginalPersonUuid == newWinner
            && value.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
        );
        next.Facts.CandidatePools.Should().ContainSingle();
        next.Facts.CandidatePools.SelectMany(pool => pool.ReadSet.CandidateUniverses)
            .Where(value => value.Universe == NormalizationCandidateUniverseKey.People)
            .Should()
            .OnlyContain(value => value.Generation > originalEpoch);
        (await CurrentAliases(first.Person))
            .Should()
            .Contain(alias => alias.Wording == first.Alias);
        (await CurrentAliases(newWinner))
            .Should()
            .Contain(alias => alias.Wording == otherClaim.Alias);
    }

    private async Task<CohortActorFixture> CohortActor()
    {
        var name = IndependentActorName();
        return new(await Person(name), name, IndependentActorName());
    }

    private static string CohortPersonnel(params CohortActorFixture[] actors) =>
        "Personnel:\n"
        + string.Join("\n", actors.Select(actor => $"{actor.Name} aka {actor.Alias} - vocals"));

    private static IEnumerable<SourceActorResolutionClaim> CohortStageClaims(
        SourceActorAliasPersonStage stage
    ) => stage.Facts.Inputs.SelectMany(input => input.Scopes).SelectMany(scope => scope.Claims);

    private static void AssertCompleteCohortEvidence(
        SourceActorAliasPersonStage stage,
        SourceContext source
    )
    {
        stage
            .Facts.Inputs.SelectMany(input => input.Scopes)
            .Select(scope => scope.OriginalScope)
            .Should()
            .BeEquivalentTo(
                source
                    .Resolution.Preparation!.Inputs.SelectMany(input => input.Scopes)
                    .Select(scope => scope.OriginalScope),
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        CohortStageClaims(stage)
            .Select(claim => claim.Coordinates.AssertionUuid)
            .Should()
            .BeEquivalentTo(Claims(source).Select(claim => claim.Coordinates.AssertionUuid));
    }

    private async Task<Dictionary<Guid, NormalizationSubjectState>> ReadOtherCohortOwners(
        SourceActorAliasPersonStage stage,
        Guid excludedOwner
    )
    {
        var owners = (stage.Facts.CurrentReadSet.OwnerObservations ?? [])
            .Select(value => value.SubjectUuid)
            .Concat(
                (stage.Facts.CurrentReadSet.Dependencies ?? []).Select(value => value.SubjectUuid)
            )
            .Where(uuid => uuid != excludedOwner)
            .Distinct();
        var result = new Dictionary<Guid, NormalizationSubjectState>();
        foreach (var uuid in owners)
        {
            result.Add(uuid, (await fixture.Work.GetSubjectAsync(uuid))!);
        }
        return result;
    }

    private async Task AdvanceCohortOwnerReceiptOnly(Guid person)
    {
        var key = new NormalizationSubjectKey("people", "person", person);
        var membership = await fixture.Work.GetInputContributionsAsync(key);
        var owner = membership.Owner!;
        var baseline = membership.UnmanagedBaseline!;
        var revision = await PersonRevision(person);
        var epoch = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                """
                SELECT generation FROM normalization.candidate_universe_revisions
                WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'
                """
            )
        );
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            key,
            owner.PolicyVersion,
            baseline.Resources,
            baseline.DependencySubjectUuids,
            owner.DesiredGeneration
        );
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var after = (await fixture.Work.GetSubjectAsync(key))!;
        after.DesiredGeneration.Should().Be(owner.DesiredGeneration);
        after.EditorialRevision.Should().Be(owner.EditorialRevision);
        after.PublishedWorkUuid.Should().Be(ticket.WorkUuid);
        (after.PublishedWorkUuid == owner.PublishedWorkUuid).Should().BeFalse();
        (await PersonRevision(person)).Should().Be(revision);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<long>(
                    """
                    SELECT generation FROM normalization.candidate_universe_revisions
                    WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'
                    """
                )
            )
        ).Should().Be(epoch);
    }

    private sealed record CohortActorFixture(Guid Person, string Name, string Alias);
}
