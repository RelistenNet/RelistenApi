using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Contributor capacity can recover before an unchanged binding union publishes again.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCapacityRecovery
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RetiringDuplicateBindingsRestoresCapacityWithoutChangingTheOwnerStamp()
    {
        var input = await fixture.Capture("Shared retained evidence", "people");
        var owner = new NormalizationSubjectKey("songs", "recording", Guid.NewGuid());
        var firstContributor = new NormalizationResourceKey(
            "test",
            "capacity",
            Guid.NewGuid().ToString("D")
        );
        var secondContributor = new NormalizationResourceKey(
            "test",
            "capacity",
            Guid.NewGuid().ToString("D")
        );
        foreach (var contributor in new[] { firstContributor, secondContributor })
        {
            await fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "capacity-recovery-v1",
                new(contributor, 0, [input.Resource], []),
                expectedInputRevisions: [new(input.Reference.InputRevisionUuid, input.Resource)]
            );
        }
        var before = (await fixture.Work.GetSubjectAsync(owner))!;
        var limits = new NormalizationInputContributionsLimits(2, 1);
        var failure = (
            await FluentActions
                .Invoking(() => fixture.Work.GetInputContributionsAsync(owner, limits))
                .Should()
                .ThrowAsync<NormalizationInputContributionsCapacityException>()
        ).Which;
        failure
            .Owner.Should()
            .Be(
                new NormalizationSubjectObservation(
                    before.SubjectUuid,
                    before.DesiredGeneration,
                    before.EditorialRevision,
                    before.PublishedWorkUuid
                )
            );
        failure.Membership.Should().Be(new NormalizationInputContributionsCapacityStamp(2, 2, "2"));

        // Admission releases the first contributor's keys. The second still owns the same
        // resource, so the union and owner stamp remain unchanged without publication.
        var retirement = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "capacity-recovery-v1",
            new(firstContributor, 1, [], []),
            expectedInputRevisions: []
        );
        var recovered = await fixture.Work.GetInputContributionsAsync(owner, limits);

        retirement.DesiredGeneration.Should().Be(before.DesiredGeneration);
        recovered.Owner.Should().Be(before);
        recovered.Contributions.Should().HaveCount(2);
        var repairedMembership = new NormalizationInputContributionsCapacityStamp(
            recovered.Contributions.Count,
            recovered.Contributions.Sum(contribution => (long)contribution.Resources.Count),
            recovered
                .Contributions.Sum(contribution => contribution.Revision)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        repairedMembership.Should().Be(new NormalizationInputContributionsCapacityStamp(2, 1, "3"));
        repairedMembership.Should().NotBe(failure.Membership);
        recovered
            .Contributions.Single(value => value.Contributor == firstContributor)
            .Resources.Should()
            .BeEmpty();
        recovered
            .Contributions.Single(value => value.Contributor == secondContributor)
            .Resources.Should()
            .Equal(input.Resource);
    }
}
