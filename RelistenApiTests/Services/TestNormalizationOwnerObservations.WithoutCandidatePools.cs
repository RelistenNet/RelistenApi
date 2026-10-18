using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationOwnerObservations
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task KnownOwnerFactsConsumeOnlyTheCandidatePoolActuallyRead(bool consumePool)
    {
        var owner = await Seed();
        var original = await Observe(owner.SubjectUuid);
        var ticket = await Begin(Subject(), [original], consumePool: consumePool);
        var before = await Pool();
        var input = await fixture.Capture("Unrelated canonical person", "people");
        await fixture.PublishPeople(
            new NormalizedPeopleService(fixture.Db, fixture.Work),
            input,
            new([new(Guid.NewGuid(), "Unrelated canonical person")], [], [], [])
        );
        (await Pool()).Generation.Should().BeGreaterThan(before.Generation);
        var calls = 0;

        var result = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                calls++;
                return Task.CompletedTask;
            }
        );

        result
            .Status.Should()
            .Be(
                consumePool
                    ? NormalizationPublicationStatus.Stale
                    : NormalizationPublicationStatus.Published
            );
        calls.Should().Be(consumePool ? 0 : 1);
        ticket.OwnerObservations.Should().Equal(original);
        if (!consumePool)
        {
            ticket.CandidateUniverses.Should().BeNullOrEmpty();
            (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
                .Replayed.Should()
                .BeTrue();
        }
    }

    [TestCase("desired", false)]
    [TestCase("editorial", false)]
    [TestCase("receipt", false)]
    [TestCase("desired", true)]
    [TestCase("editorial", true)]
    [TestCase("receipt", true)]
    public async Task PoolFreeOwnerReadsStillFenceEveryOriginalStamp(
        string field,
        bool afterAdmission
    )
    {
        var owner = await Seed();
        var original = await Observe(owner.SubjectUuid);
        var target = Subject();
        if (afterAdmission)
        {
            var ticket = await Begin(target, [original], consumePool: false);
            await Change(owner, field);

            (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Stale);
        }
        else
        {
            await Change(owner, field);
            var begin = () => Begin(target, [original], consumePool: false);

            await begin.Should().ThrowAsync<NormalizationConflictException>();
            (await fixture.Work.GetSubjectAsync(target)).Should().BeNull();
        }
    }

    [Test]
    public async Task ContributedOwnerFactsPublishWithoutAPoolAndReplayTheirOriginalAdmission()
    {
        var owner = await Seed();
        var original = await Observe(owner.SubjectUuid);
        var target = Subject();
        var contributor = Contributor();
        var operation = Guid.NewGuid();
        NormalizationPreparedInputContribution[] contributions =
        [
            new(
                new(contributor, 0, [], [], [original.SubjectUuid]),
                [],
                ExpectedOwnerObservations: [original]
            ),
        ];
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            operation,
            target,
            "observation-v1",
            contributions,
            expectedContributionRevisions: []
        );
        ticket.CandidateUniverses.Should().BeNullOrEmpty();
        ticket.OwnerObservations.Should().Equal(original);
        await Publish(ticket);
        await Change(owner, "editorial");
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();

        var replay = await fixture.Work.BeginContributedBatchAsync(
            operation,
            target,
            "observation-v1",
            contributions,
            expectedContributionRevisions: []
        );

        replay.Should().BeEquivalentTo(ticket);
        (await fixture.Work.TryPublishAsync(operation, NeverWrite)).Replayed.Should().BeTrue();
        var membership = (await fixture.Work.GetInputContributionAsync(target, contributor))!;
        membership.Revision.Should().Be(1);
        membership.OwnerObservationSubjectUuids.Should().Equal(original.SubjectUuid);
    }
}
