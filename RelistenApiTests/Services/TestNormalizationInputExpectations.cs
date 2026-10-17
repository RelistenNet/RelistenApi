using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Admission must not attach an already computed binding to newer retained provider inputs.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationInputExpectations
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task StaleExpectedHeadRollsBackContributionAndAllowsSameWorkUuidToRetry()
    {
        var input = await fixture.Capture("first", "people");
        var original = new NormalizationBindingInputReference(
            input.Reference.InputRevisionUuid,
            input.Resource
        );
        var current = await Advance(input.Resource);
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var stale = () => Begin(workUuid, subject, contributor, [input.Resource], [original]);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();

        var ticket = await Begin(workUuid, subject, contributor, [input.Resource], [current]);
        ticket
            .Inputs.Should()
            .ContainSingle()
            .Which.RevisionUuid.Should()
            .Be(current.InputRevisionUuid);
        (await fixture.Work.GetInputContributionAsync(subject, contributor))!
            .Revision.Should()
            .Be(1);
    }

    [Test]
    public async Task ExpectedReadSetIsOrderIndependentForReplayAndCannotBeChangedForSameWorkUuid()
    {
        var a = await fixture.Capture("A", "people");
        var b = await fixture.Capture("B", "people");
        var expectedA = new NormalizationBindingInputReference(
            a.Reference.InputRevisionUuid,
            a.Resource
        );
        var expectedB = new NormalizationBindingInputReference(
            b.Reference.InputRevisionUuid,
            b.Resource
        );
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var ticket = await Begin(
            workUuid,
            subject,
            contributor,
            [a.Resource, b.Resource],
            [expectedA, expectedB]
        );
        var currentA = await Advance(a.Resource);

        (
            await Begin(
                workUuid,
                subject,
                contributor,
                [a.Resource, b.Resource],
                [expectedB, expectedA, expectedA]
            )
        )
            .Should()
            .BeEquivalentTo(ticket);
        var changedReplay = () =>
            Begin(workUuid, subject, contributor, [a.Resource, b.Resource], [currentA, expectedB]);
        await changedReplay.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ContributorCannotPinAnInputOwnedOnlyByAnotherContributor()
    {
        var a = await fixture.Capture("A", "people");
        var b = await fixture.Capture("B", "people");
        var subject = Subject();
        var other = Contributor();
        await Begin(Guid.NewGuid(), subject, Contributor(), [a.Resource], []);
        var outsideOwnership = () =>
            Begin(
                Guid.NewGuid(),
                subject,
                other,
                [b.Resource],
                [new(a.Reference.InputRevisionUuid, a.Resource)]
            );

        await outsideOwnership.Should().ThrowAsync<ArgumentException>();
        (await fixture.Work.GetInputContributionAsync(subject, other)).Should().BeNull();
    }

    [Test]
    public async Task ExpectedRevisionMustMatchItsOwnResourceRatherThanAnyOtherOwnedResource()
    {
        var a = await fixture.Capture("A", "people");
        var b = await fixture.Capture("B", "people");
        var subject = Subject();
        var contributor = Contributor();
        var swapped = () =>
            Begin(
                Guid.NewGuid(),
                subject,
                contributor,
                [a.Resource, b.Resource],
                [
                    new(b.Reference.InputRevisionUuid, a.Resource),
                    new(a.Reference.InputRevisionUuid, b.Resource),
                ]
            );

        await swapped.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
    }

    [Test]
    public async Task ConflictingAndEmptyRevisionExpectationsAreRejectedBeforeAdmission()
    {
        var input = await fixture.Capture("A", "people");
        var subject = Subject();
        var contributor = Contributor();
        var empty = () =>
            Begin(
                Guid.NewGuid(),
                subject,
                contributor,
                [input.Resource],
                [new(Guid.Empty, input.Resource)]
            );
        await empty.Should().ThrowAsync<ArgumentException>();
        var conflicting = () =>
            Begin(
                Guid.NewGuid(),
                subject,
                contributor,
                [input.Resource],
                [
                    new(input.Reference.InputRevisionUuid, input.Resource),
                    new(Guid.NewGuid(), input.Resource),
                ]
            );
        await conflicting.Should().ThrowAsync<ArgumentException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
    }

    [Test]
    public async Task EmptyExpectationsKeepExistingRunReplayIdentity()
    {
        var input = await fixture.Capture("A", "people");
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var original = await Begin(workUuid, subject, contributor, [input.Resource], null);

        (await Begin(workUuid, subject, contributor, [input.Resource], []))
            .Should()
            .BeEquivalentTo(original);
    }

    [Test]
    public async Task DirectStaleAdmissionPreservesPublishedPolicyAndCompleteInputMembership()
    {
        var root = await fixture.Capture("root map", "people");
        var directory = await fixture.Capture("directory credits", "people");
        var subject = Subject();
        var initial = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "existing-policy",
            [root.Resource, directory.Resource],
            [],
            expectedInputRevisions:
            [
                new(root.Reference.InputRevisionUuid, root.Resource),
                new(directory.Reference.InputRevisionUuid, directory.Resource),
            ]
        );
        await fixture.Work.TryPublishAsync(
            initial.WorkUuid,
            (connection, transaction, ct) => Task.CompletedTask
        );
        var published = await fixture.Work.GetSubjectAsync(initial.SubjectUuid);
        await Advance(root.Resource);

        // This proposal changes policy and removes the directory binding. Stale root evidence
        // must reject the entire replacement, including its otherwise valid configuration CAS.
        var staleReplacement = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                subject,
                "replacement-policy",
                [root.Resource],
                [],
                initial.DesiredGeneration,
                expectedInputRevisions: [new(root.Reference.InputRevisionUuid, root.Resource)]
            );
        await staleReplacement.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(initial.SubjectUuid))
            .Should()
            .BeEquivalentTo(published);

        var stillConfigured = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "existing-policy",
            [root.Resource, directory.Resource],
            []
        );
        stillConfigured.DesiredGeneration.Should().Be(initial.DesiredGeneration);
        stillConfigured.PreviousPublishedWorkUuid.Should().Be(initial.WorkUuid);
        stillConfigured
            .Inputs.Select(input => input.ResourceUuid)
            .Should()
            .BeEquivalentTo(initial.Inputs.Select(input => input.ResourceUuid));
        Guid? after = null;
        var pending = false;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            pending = page.Any(row => row.SubjectUuid == initial.SubjectUuid);
            if (pending || page.Count < 1000)
            {
                break;
            }
            after = page[^1].SubjectUuid;
        }
        pending.Should().BeTrue("the changed root still requires a new publication");
    }

    private Task<NormalizationWorkTicket> Begin(
        Guid workUuid,
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        IReadOnlyList<NormalizationResourceKey> resources,
        IReadOnlyList<NormalizationBindingInputReference>? expected
    ) =>
        fixture.Work.BeginContributedWorkAsync(
            workUuid,
            subject,
            "input-expectations-v1",
            new(contributor, 0, resources, []),
            expectedInputRevisions: expected
        );

    private async Task<NormalizationBindingInputReference> Advance(
        NormalizationResourceKey resource
    )
    {
        var capture = await new NormalizationEvidenceService(fixture.Db).CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes("{\"text\":\"changed\"}"),
                "application/json",
                "test",
                1,
                DateTimeOffset.UtcNow
            )
        );
        return new(capture.Revision.Uuid, resource);
    }

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("test", "input-expectations", Guid.NewGuid().ToString("D"));
}
