using System.Text;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// A retained revision can become current more than once. Generation-pinned admission must
/// distinguish those observations, while omitted generations retain revision-only replay.
/// Both unmanaged and contributed commands use the same input-lock and rollback contract.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationWorkInputGenerations
{
    private const string Policy = "generation-fence-fixture-v1";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReturnedRevisionAtANewGenerationCannotAdmitAnOldObservation(bool contributed)
    {
        var original = await Capture("A");
        var second = await Capture("B", original);
        var returned = await Capture("A", second);
        returned.Revision.Should().Be(original.Revision);
        returned.Generation.Should().Be(original.Generation + 2);
        var subject = Subject();
        var workUuid = Guid.NewGuid();
        var contributor = Contributor();

        await Conflict(() => Begin(contributed, subject, workUuid, contributor, original));

        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
        (await fixture.Work.GetInputContributionsAsync(subject)).Contributions.Should().BeEmpty();
        (await RunExists(workUuid)).Should().BeFalse();
        var admitted = await Begin(contributed, subject, workUuid, contributor, returned);
        admitted
            .Inputs.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new NormalizationInputDependency(
                    returned.ResourceUuid,
                    returned.Revision,
                    returned.Generation
                )
            );
        (await fixture.Work.TryPublishAsync(workUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        await Capture("C", returned);

        (await Begin(contributed, subject, workUuid, contributor, returned))
            .Should()
            .BeEquivalentTo(admitted);
        (
            await fixture.Work.TryPublishAsync(
                workUuid,
                (_, _, _) =>
                    throw new AssertionException(
                        "Committed work must replay without another publication"
                    )
            )
        )
            .Replayed.Should()
            .BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RefusedReconfigurationDoesNotChangeOwnerBindingsOrContributorRevision(
        bool contributed
    )
    {
        var original = await Capture("Original A");
        var subject = Subject();
        var contributor = Contributor();
        await Begin(contributed, subject, Guid.NewGuid(), contributor, original);
        var before = await fixture.Work.GetInputContributionsAsync(subject);
        var owner = before.Owner!;
        var rawOwner = await RawOwner(owner.SubjectUuid);
        var different = await Capture("Different B", original);
        var returned = await Capture("Original A", different);
        returned.Revision.Should().Be(original.Revision);
        var workUuid = Guid.NewGuid();
        var expectedContribution = contributed ? 1 : 0;

        await Conflict(() =>
            Begin(
                contributed,
                subject,
                workUuid,
                contributor,
                original,
                expectedContribution,
                owner.DesiredGeneration,
                Policy + "/changed"
            )
        );

        (await fixture.Work.GetInputContributionsAsync(subject)).Should().BeEquivalentTo(before);
        (await RawOwner(owner.SubjectUuid)).Should().Be(rawOwner);
        (await RunExists(workUuid)).Should().BeFalse();
        var admitted = await Begin(
            contributed,
            subject,
            workUuid,
            contributor,
            returned,
            expectedContribution,
            owner.DesiredGeneration,
            Policy + "/changed"
        );
        admitted.Inputs.Single().Generation.Should().Be(returned.Generation);
        (await fixture.Work.GetSubjectAsync(subject))!
            .PolicyVersion.Should()
            .Be(Policy + "/changed");
        if (contributed)
        {
            (await fixture.Work.GetInputContributionAsync(subject, contributor))!
                .Revision.Should()
                .Be(2);
        }
    }

    [TestCase(false, "foreign_resource")]
    [TestCase(false, "foreign_revision")]
    [TestCase(false, "wrong_generation")]
    [TestCase(true, "foreign_resource")]
    [TestCase(true, "foreign_revision")]
    [TestCase(true, "wrong_generation")]
    public async Task ExpectationsBindTheResourceRevisionAndGenerationTogether(
        bool contributed,
        string fault
    )
    {
        var original = await Capture("Owned input");
        var other = await Capture("Different resource");
        var expected = fault switch
        {
            "foreign_resource" => new NormalizationBindingInputReference(
                original.Revision,
                other.Resource,
                original.Generation
            ),
            "foreign_revision" => new(other.Revision, original.Resource, original.Generation),
            "wrong_generation" => new(
                original.Revision,
                original.Resource,
                original.Generation + 1
            ),
            _ => throw new AssertionException("Unknown input expectation case"),
        };
        var subject = Subject();
        var workUuid = Guid.NewGuid();
        Func<Task> begin = () =>
            BeginWithExpectations(
                contributed,
                subject,
                workUuid,
                Contributor(),
                original,
                [expected]
            );

        var failure = await begin.Should().ThrowAsync<Exception>();

        HasCause(
                failure.Which,
                fault == "foreign_resource"
                    ? typeof(ArgumentException)
                    : typeof(NormalizationConflictException)
            )
            .Should()
            .BeTrue();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
        (await RunExists(workUuid)).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OmittedGenerationKeepsRevisionOnlyAdmissionAcrossAReturnToTheSameBytes(
        bool contributed
    )
    {
        var original = await Capture("Legacy A");
        var changed = await Capture("Legacy B", original);
        var returned = await Capture("Legacy A", changed);
        var ticket = await BeginWithExpectations(
            contributed,
            Subject(),
            Guid.NewGuid(),
            Contributor(),
            original,
            [new(original.Revision, original.Resource)]
        );

        ticket.Inputs.Single().RevisionUuid.Should().Be(original.Revision);
        ticket.Inputs.Single().Generation.Should().Be(returned.Generation);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SuppliedGenerationIsPartOfAnAdmittedRequestsReplayIdentity(bool contributed)
    {
        var input = await Capture("Pinned request identity");
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var ticket = await Begin(contributed, subject, workUuid, contributor, input);

        await Conflict(() =>
            BeginWithExpectations(
                contributed,
                subject,
                workUuid,
                contributor,
                input,
                [new(input.Revision, input.Resource)]
            )
        );
        await Conflict(() =>
            BeginWithExpectations(
                contributed,
                subject,
                workUuid,
                contributor,
                input,
                [new(input.Revision, input.Resource, input.Generation + 1)]
            )
        );

        (await Begin(contributed, subject, workUuid, contributor, input))
            .Should()
            .BeEquivalentTo(ticket);
    }

    [Test]
    public async Task SharedResourcesWithIdenticalGenerationsMergeWithoutLosingContributorOwnership()
    {
        var input = await Capture("Shared observed input");
        var subject = Subject();
        var first = Contributor();
        var second = Contributor();
        var expected = new NormalizationBindingInputReference(
            input.Revision,
            input.Resource,
            input.Generation
        );
        var workUuid = Guid.NewGuid();
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            workUuid,
            subject,
            Policy,
            [Prepared(first, input, [expected, expected]), Prepared(second, input, [expected])]
        );

        ticket.Inputs.Should().ContainSingle();
        ticket.Inputs[0].Generation.Should().Be(input.Generation);
        var membership = await fixture.Work.GetInputContributionsAsync(subject);
        membership.Contributions.Should().HaveCount(2);
        membership
            .Contributions.Should()
            .OnlyContain(contribution => contribution.Resources.Count == 1);
        (
            await fixture.Work.BeginContributedBatchAsync(
                workUuid,
                subject,
                Policy,
                [Prepared(first, input, [expected]), Prepared(second, input, [expected])]
            )
        )
            .Should()
            .BeEquivalentTo(ticket);
    }

    private async Task<Input> Capture(
        string text,
        Input? previous = null,
        CancellationToken ct = default
    )
    {
        var resource =
            previous?.Resource ?? new("test", "generation-input", Guid.NewGuid().ToString("N"));
        var result = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes(text),
                "text/plain",
                "fixture",
                previous?.Generation ?? 0,
                DateTimeOffset.UtcNow
            ),
            ct
        );
        result.Fetch.AcceptedAsCurrent.Should().BeTrue();
        var resourceUuid = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT resource_uuid FROM normalization.input_revisions WHERE uuid=@uuid",
                new { uuid = result.Revision.Uuid }
            )
        );
        return new(resource, resourceUuid, result.Revision.Uuid, result.Fetch.ResourceGeneration);
    }

    private Task<NormalizationWorkTicket> Begin(
        bool contributed,
        NormalizationSubjectKey subject,
        Guid workUuid,
        NormalizationResourceKey contributor,
        Input input,
        long contributorRevision = 0,
        long? configurationGeneration = null,
        string policy = Policy,
        CancellationToken ct = default
    ) =>
        BeginWithExpectations(
            contributed,
            subject,
            workUuid,
            contributor,
            input,
            [new(input.Revision, input.Resource, input.Generation)],
            contributorRevision,
            configurationGeneration,
            policy,
            ct
        );

    private Task<NormalizationWorkTicket> BeginWithExpectations(
        bool contributed,
        NormalizationSubjectKey subject,
        Guid workUuid,
        NormalizationResourceKey contributor,
        Input input,
        IReadOnlyList<NormalizationBindingInputReference> expected,
        long contributorRevision = 0,
        long? configurationGeneration = null,
        string policy = Policy,
        CancellationToken ct = default
    ) =>
        contributed
            ? fixture.Work.BeginContributedBatchAsync(
                workUuid,
                subject,
                policy,
                [Prepared(contributor, input, expected, contributorRevision)],
                configurationGeneration,
                ct
            )
            : fixture.Work.BeginWorkAsync(
                workUuid,
                subject,
                policy,
                [input.Resource],
                [],
                configurationGeneration,
                ct,
                expectedInputRevisions: expected
            );

    private static NormalizationPreparedInputContribution Prepared(
        NormalizationResourceKey contributor,
        Input input,
        IReadOnlyList<NormalizationBindingInputReference> expected,
        long revision = 0
    ) => new(new(contributor, revision, [input.Resource], []), expected);

    private Task<string> RawOwner(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT to_jsonb(owner)::text FROM normalization.work_subjects owner WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<bool> RunExists(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.work_runs WHERE uuid=@uuid)",
                new { uuid }
            )
        );

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("test", "generation-contributor", Guid.NewGuid().ToString("N"));

    private static bool HasCause(Exception failure, Type type) =>
        type.IsInstanceOfType(failure)
        || failure.InnerException != null && HasCause(failure.InnerException, type);

    private static async Task Conflict(Func<Task> action)
    {
        var failure = await action.Should().ThrowAsync<Exception>();
        HasCause(failure.Which, typeof(NormalizationConflictException)).Should().BeTrue();
    }

    private sealed record Input(
        NormalizationResourceKey Resource,
        Guid ResourceUuid,
        Guid Revision,
        long Generation
    );
}
