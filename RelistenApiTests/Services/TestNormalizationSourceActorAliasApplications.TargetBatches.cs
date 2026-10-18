using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task DistinctReviewedOriginalsSharingAMultihopTerminalRetainCompleteOrderedTargetsThroughRecovery()
    {
        var batch = await ReviewedTargetBatch();
        var saved = await PrepareAdmittedSourceStage(batch.Source, batch.Originals[0]);
        var first = saved.Stages.Single();
        AssertFirstTargetBatch(first, batch);
        var unrelatedState = await NonAliasState();
        var terminalAliases = await CurrentAliases(batch.Terminal);

        (await people.PublishSourceActorAliasStageAsync(saved.AttemptUuid, first.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var committed = (await applications.GetStageAsync(first.StageUuid))!;
        committed.Receipt!.OriginalPersonUuid.Should().Be(batch.Originals[0]);
        committed
            .Facts.Should()
            .BeEquivalentTo(
                first.Facts,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );

        var completed = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, batch.Source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        completed
            .Stages.Where(stage =>
                stage.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            )
            .Select(stage => stage.OriginalPersonUuid)
            .Should()
            .Equal(batch.Originals);
        foreach (var stage in completed.Stages)
        {
            stage
                .Facts.Targets.Select(target => target.OriginalPersonUuid)
                .Should()
                .Equal(
                    batch
                        .Originals.Where(person => person != stage.OriginalPersonUuid)
                        .Append(stage.OriginalPersonUuid)
                );
            stage.Facts.Targets.Last().OriginalPersonUuid.Should().Be(stage.OriginalPersonUuid);
            foreach (var target in stage.Facts.Targets)
            {
                var original = batch.Targets.Single(value =>
                    value.OriginalPersonUuid == target.OriginalPersonUuid
                );
                target.ResolvedPersonUuid.Should().Be(original.ResolvedPersonUuid);
                target
                    .Members.Select(member => (member.PersonUuid, member.ResolvedPersonUuid))
                    .Should()
                    .Equal(
                        original.Members.Select(member =>
                            (member.PersonUuid, member.ResolvedPersonUuid)
                        )
                    );
                foreach (var member in target.Members)
                {
                    member
                        .Revision.Should()
                        .BeGreaterThanOrEqualTo(
                            original
                                .Members.Single(value => value.PersonUuid == member.PersonUuid)
                                .Revision
                        );
                }
                target
                    .Redirects.Should()
                    .BeEquivalentTo(original.Redirects, options => options.WithStrictOrdering());
                target
                    .OwnerObservations.Select(owner => owner.SubjectUuid)
                    .Should()
                    .Equal(original.OwnerObservations.Select(owner => owner.SubjectUuid));
                stage
                    .Facts.CurrentReadSet.OwnerObservations.Should()
                    .Contain(target.OwnerObservations);
            }
            (await applications.GetStageAsync(stage.StageUuid))
                .Should()
                .BeEquivalentTo(
                    stage,
                    options => options.WithStrictOrdering().AllowingInfiniteRecursion()
                );
        }
        var initialRevision = batch
            .Targets[0]
            .Members.Single(member => member.PersonUuid == batch.Originals[0])
            .Revision;
        completed
            .Stages.Skip(1)
            .SelectMany(stage => stage.Facts.Targets)
            .SelectMany(target => target.Members)
            .Should()
            .Contain(
                member =>
                    member.PersonUuid == batch.Originals[0] && member.Revision > initialRevision,
                "accepted assignments and support advance the original person's revision"
            );
        completed
            .Stages.Single(stage => stage.StageUuid == committed.StageUuid)
            .Should()
            .BeEquivalentTo(
                committed,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        for (var index = 0; index < batch.Originals.Length; index++)
        {
            (await CurrentAliases(batch.Originals[index]))
                .Should()
                .Contain(alias => alias.Wording == batch.Aliases[index]);
        }
        (await CurrentAliases(batch.Terminal))
            .Should()
            .BeEquivalentTo(
                terminalAliases,
                "the common resolved identity does not replace either selected original alias owner"
            );
        (await NonAliasState()).Should().Be(unrelatedState);
        var accepted = await AcceptedAuthority();

        var replay = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, batch.Source.Resolution.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        replay
            .Stages.Should()
            .BeEquivalentTo(
                completed.Stages,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        (await AcceptedAuthority()).Should().Be(accepted);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SavedTargetBatchRefusesPublicationAfterAnotherComponentOwnerOrRedirectChanges(
        bool reverseRedirect
    )
    {
        var batch = await ReviewedTargetBatch();
        var saved = await PrepareAdmittedSourceStage(batch.Source, batch.Originals[0]);
        var stage = saved.Stages.Single();
        AssertFirstTargetBatch(stage, batch);
        if (reverseRedirect)
        {
            var middleOwner = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("people", "person", batch.Originals[1])
                )
            )!;
            await people.ReverseMergeAsync(
                new(
                    Guid.NewGuid(),
                    middleOwner.SubjectUuid,
                    middleOwner.EditorialRevision,
                    batch.TerminalMergeUuid,
                    batch.Originals[1],
                    batch.Terminal,
                    await PersonRevision(batch.Originals[1]),
                    await PersonRevision(batch.Terminal),
                    "Separate the shared terminal after the alias target proof was saved"
                ),
                Operator
            );
        }
        else
        {
            await ReviewCanonicalAlias(batch.Terminal, IndependentActorName(), "accept");
        }
        // The public correction above intentionally changes its own authority. Only the
        // subsequent alias publication must leave these corrected rows untouched.
        var correctedAuthority = await AcceptedAuthority();

        var publication = await people.PublishSourceActorAliasStageAsync(
            saved.AttemptUuid,
            stage.StageUuid
        );

        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        var retained = (await applications.GetStageAsync(stage.StageUuid))!;
        retained
            .Facts.Should()
            .BeEquivalentTo(
                stage.Facts,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        retained
            .Ticket.Should()
            .BeEquivalentTo(stage.Ticket, options => options.WithStrictOrdering());
        retained.Receipt.Should().BeNull();
        var refused = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, batch.Source.Resolution.AttemptUuid);
        refused.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        Receipts(refused).Should().BeEmpty();
        (await ActiveAssignments(batch.Source.Source.SourceUuid)).Should().Be(0);
        (await AcceptedAuthority()).Should().Be(correctedAuthority);
    }

    private async Task<TargetBatchFixture> ReviewedTargetBatch()
    {
        var names = Enumerable.Range(0, 4).Select(_ => IndependentActorName()).ToArray();
        var persons = new List<Guid>();
        foreach (var name in names)
        {
            persons.Add(await Person(name));
        }
        await MergeTargetBatchPeople(persons[0], persons[1], names[1]);
        var terminalMerge = await MergeTargetBatchPeople(persons[1], persons[2], names[2]);
        var originals = new[] { persons[0], persons[1], persons[3] };
        var aliases = originals.Select(_ => IndependentActorName()).ToArray();
        var claimNames = new[] { names[0], names[1], names[3] };
        var source = await Source(
            "Personnel:\n"
                + string.Join(
                    "\n",
                    claimNames.Select((name, index) => $"{name} aka {aliases[index]} - vocals")
                )
        );
        Claims(source).Should().HaveCount(3);
        var targets = new List<SourceActorReviewTarget>();
        for (var index = 0; index < originals.Length; index++)
        {
            var claim = FindSourceClaim(source, claimNames[index]);
            var owner = (
                await fixture.Work.GetSubjectAsync(
                    Relisten.Data.SourceActorResolutionService.Owner(source.Source.SourceUuid)
                )
            )!;
            var prepared = await reviews.PrepareAsync(
                Guid.NewGuid(),
                new(
                    source.Extraction.AttemptUuid,
                    claim.Coordinates,
                    SourceActorReviewAction.Accept,
                    owner.EditorialRevision,
                    0,
                    originals[index],
                    await PersonRevision(originals[index])
                )
            );
            await reviews.ApplyAsync(
                Guid.NewGuid(),
                prepared.PreparationUuid,
                "Select this original identity while retaining the complete shared component",
                Operator
            );
            targets.Add(prepared.Target!);
        }
        source = await Resolve(source.Source);
        return new(source, originals, aliases, targets.ToArray(), persons[2], terminalMerge);
    }

    private async Task<Guid> MergeTargetBatchPeople(
        Guid original,
        Guid survivor,
        string survivorName
    )
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original)
            )
        )!;
        var decision = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                decision,
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original,
                await PersonRevision(original),
                new(survivor, survivorName),
                await PersonRevision(survivor),
                [],
                "Establish the reviewed multihop target component"
            ),
            Operator
        );
        return decision;
    }

    private static void AssertFirstTargetBatch(
        SourceActorAliasPersonStage stage,
        TargetBatchFixture batch
    )
    {
        stage.OriginalPersonUuid.Should().Be(batch.Originals[0]);
        var expected = batch.Targets.Skip(1).Append(batch.Targets[0]).ToArray();
        // Review preparations and this first stage have no intervening person writes.
        // Admission retains the primary's original observation in both proof and ticket.
        stage
            .Facts.Targets.Should()
            .BeEquivalentTo(expected, options => options.WithStrictOrdering());
        stage.Facts.Targets[0].ResolvedPersonUuid.Should().Be(batch.Terminal);
        stage.Facts.Targets.Last().ResolvedPersonUuid.Should().Be(batch.Terminal);
        stage.Facts.Targets.Last().Members.Should().HaveCount(3);
        stage.Facts.Targets.Last().Redirects.Should().HaveCount(2);
        stage.Facts.Policy.Should().BeNull();
        stage.Facts.CandidatePools.Should().BeEmpty();
        var owners = expected.SelectMany(target => target.OwnerObservations).Distinct().ToArray();
        owners.Should().HaveCount(4);
        stage.Facts.CurrentReadSet.OwnerObservations.Should().Contain(owners);
        stage.Ticket!.OwnerObservations.Should().Contain(owners);
    }

    private sealed record TargetBatchFixture(
        SourceContext Source,
        Guid[] Originals,
        string[] Aliases,
        SourceActorReviewTarget[] Targets,
        Guid Terminal,
        Guid TerminalMergeUuid
    );
}
