using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationOwnerObservations
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task PersonCanObserveItsRecordingCreditOwnerWithoutReversingTheCausalEdge()
    {
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var people = new NormalizedPeopleService(fixture.Db, fixture.Work);
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        var personOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", person.Uuid)
            )
        )!;
        var source = await fixture.Source();
        var recording = await fixture.Own(
            input,
            "recording",
            source.SourceUuid,
            [personOwner.SubjectUuid]
        );
        await people.PublishAsync(
            recording.Ticket.WorkUuid,
            new(
                [],
                [],
                [],
                [
                    new(
                        Guid.NewGuid(),
                        source.SourceUuid,
                        "taper",
                        "Pat",
                        person.Uuid,
                        null,
                        [input.Reference]
                    ),
                ]
            )
        );
        var observation = await Observe(recording.Ticket.SubjectUuid);
        var next = await Begin(personOwner.Subject, [observation], resources: [input.Resource]);
        next.Dependencies.Should().BeEmpty();
        next.OwnerObservations.Should().Equal(observation);
        await Publish(next);
        (await HasEdge(personOwner.SubjectUuid, recording.Ticket.SubjectUuid)).Should().BeFalse();
        (await HasEdge(recording.Ticket.SubjectUuid, personOwner.SubjectUuid)).Should().BeTrue();
        var cycle = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                personOwner.Subject,
                "observation-v1",
                [input.Resource],
                [recording.Ticket.SubjectUuid],
                next.DesiredGeneration
            );
        await cycle.Should().ThrowAsync<ArgumentException>().WithMessage("*acyclic*");
    }

    [Test]
    public async Task SelfObservationKeepsOriginalStampWhilePublicationUsesConfiguredPrimaryState()
    {
        var seed = await Seed();
        var observed = await Observe(seed.SubjectUuid);
        var ticket = await Begin(await SubjectOf(seed), [observed]);
        ticket.DesiredGeneration.Should().BeGreaterThan(observed.DesiredGeneration);
        ticket.OwnerObservations.Should().Equal(observed);
        ticket.Dependencies.Should().BeEmpty();
        await Publish(ticket);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
        var stored = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT ticket::text FROM normalization.work_runs WHERE uuid=@WorkUuid",
                ticket
            )
        );
        JsonSerializer
            .Deserialize<NormalizationWorkTicket>(stored)!
            .OwnerObservations.Should()
            .Equal(observed);
        var selfSubject = await SubjectOf(seed);
        var selfEdge = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                selfSubject,
                "observation-v1",
                [],
                [seed.SubjectUuid],
                ticket.DesiredGeneration
            );
        await selfEdge.Should().ThrowAsync<ArgumentException>().WithMessage("*itself*");
        await Change(ticket, "editorial");
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        var reviewedObservation = await Observe(ticket.SubjectUuid);
        var refreshed = await Begin(selfSubject, [reviewedObservation]);
        await Publish(refreshed);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
        refreshed.OwnerObservations.Should().Equal(reviewedObservation);
    }

    [TestCase("desired")]
    [TestCase("editorial")]
    [TestCase("receipt")]
    public async Task ChangedOriginalOwnerStampRejectsAdmissionBeforeConfiguration(string field)
    {
        var owner = await Seed();
        var original = await Observe(owner.SubjectUuid);
        var pool = await Pool();
        await Change(owner, field);
        var target = Subject();
        var begin = () => Begin(target, [original], pool: pool);
        await begin.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(target)).Should().BeNull();
        (await Pool()).Should().Be(pool);
    }

    [TestCase("desired")]
    [TestCase("editorial")]
    [TestCase("receipt")]
    public async Task ChangedOwnerStampMakesInFlightWorkStaleWithoutInvokingCallback(string field)
    {
        var owner = await Seed();
        var ticket = await Begin(Subject(), [await Observe(owner.SubjectUuid)]);
        await Change(owner, field);
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [TestCase("desired")]
    [TestCase("editorial")]
    public async Task ConfigurationAndEditorialChangesMakePublishedObserversPendingWithoutEpochChanges(
        string field
    )
    {
        var owner = await Seed();
        var ticket = await Begin(Subject(), [await Observe(owner.SubjectUuid)]);
        await Publish(ticket);
        var pool = await Pool();
        await Change(owner, field);
        (await Pool()).Should().Be(pool);
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task ReceiptOnlyChangeLeavesPublishedObservationCurrentButFencesAnInFlightDecision()
    {
        var owner = await Seed();
        var observation = await Observe(owner.SubjectUuid);
        var published = await Begin(Subject(), [observation]);
        await Publish(published);
        var running = await Begin(Subject(), [observation]);
        await Change(owner, "receipt");
        (await Pending(published.SubjectUuid)).Should().BeFalse();
        (await fixture.Work.TryPublishAsync(running.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task MissingObservedOwnerCannotBeAdmitted()
    {
        var target = Subject();
        var unknown = () => Begin(target, [new(Guid.NewGuid(), 0, 0, null)]);
        await unknown.Should().ThrowAsync<ArgumentException>();
        (await fixture.Work.GetSubjectAsync(target)).Should().BeNull();
    }

    [Test]
    public async Task ReplayRetainsOriginalObservationAndRejectsChangedStampForTheSameWorkUuid()
    {
        var owner = await Seed();
        var target = Subject();
        var original = await Observe(owner.SubjectUuid);
        var pool = await Pool();
        var operation = Guid.NewGuid();
        var ticket = await fixture.Work.BeginWorkAsync(
            operation,
            target,
            "observation-v1",
            [],
            [],
            candidateUniverses: [pool],
            expectedOwnerObservations: [original]
        );
        await Publish(ticket);
        await Change(owner, "receipt");
        var replay = await fixture.Work.BeginWorkAsync(
            operation,
            target,
            "observation-v1",
            [],
            [],
            candidateUniverses: [pool],
            expectedOwnerObservations: [original]
        );
        replay.OwnerObservations.Should().Equal(original);
        (await fixture.Work.TryPublishAsync(operation, NeverWrite)).Replayed.Should().BeTrue();
        var changed = await Observe(owner.SubjectUuid);
        var mismatched = () =>
            fixture.Work.BeginWorkAsync(
                operation,
                target,
                "observation-v1",
                [],
                [],
                candidateUniverses: [pool],
                expectedOwnerObservations: [changed]
            );
        await mismatched.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ContributionsOwnObservationKeysAndRequireOriginalStampsForTheExactUnion()
    {
        var firstOwner = await Seed();
        var secondOwner = await Seed();
        var first = await Observe(firstOwner.SubjectUuid);
        var second = await Observe(secondOwner.SubjectUuid);
        var target = Subject();
        var a = Contributor();
        var b = Contributor();
        var admittedA = await Contribute(target, a, 0, [first.SubjectUuid], [first]);
        await Publish(admittedA);
        var missingPeer = () => Contribute(target, b, 0, [second.SubjectUuid], [second]);
        await missingPeer
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*complete binding union*");
        (await fixture.Work.GetInputContributionAsync(target, b)).Should().BeNull();
        var admittedB = await Contribute(target, b, 0, [second.SubjectUuid], [first, second]);
        admittedB.OwnerObservations.Should().BeEquivalentTo(new[] { first, second });
        await Publish(admittedB);
        var partialOrdinary = () => Begin(target, [second]);
        await partialOrdinary
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*complete binding union*");
        var removeA = await Contribute(target, a, 1, [], [second], ownPool: false);
        removeA.OwnerObservations.Should().Equal(second);
        await Publish(removeA);
        var tombstone = (await fixture.Work.GetInputContributionAsync(target, a))!;
        tombstone.Revision.Should().Be(2);
        tombstone.OwnerObservationSubjectUuids.Should().BeNullOrEmpty();
        (await fixture.Work.GetInputContributionAsync(target, b))!
            .OwnerObservationSubjectUuids.Should()
            .Equal(second.SubjectUuid);
    }

    [Test]
    public async Task ContributedAdmissionNeverRefreshesAnotherContributorsStaleObservation()
    {
        var owner = await Seed();
        var original = await Observe(owner.SubjectUuid);
        var target = Subject();
        var a = Contributor();
        var b = Contributor();
        await Contribute(target, a, 0, [original.SubjectUuid], [original]);
        await Change(owner, "receipt");
        var stalePeer = () => Contribute(target, b, 0, [], [original]);
        await stalePeer
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*Observed owners changed*");
        (await fixture.Work.GetInputContributionAsync(target, b)).Should().BeNull();
        (await fixture.Work.GetInputContributionAsync(target, a))!.Revision.Should().Be(1);
        var fresh = await Observe(owner.SubjectUuid);
        var admitted = await Contribute(target, b, 0, [], [fresh]);
        admitted.OwnerObservations.Should().Equal(fresh);
    }

    [Test]
    public async Task FirstContributionAdoptsUnmanagedObservationMembershipAsBaseline()
    {
        var first = await Observe((await Seed()).SubjectUuid);
        var second = await Observe((await Seed()).SubjectUuid);
        var target = Subject();
        var ordinary = await Begin(target, [first]);
        await Publish(ordinary);
        var contributor = Contributor();
        await Contribute(target, contributor, 0, [second.SubjectUuid], [first, second]);
        var tombstone = await Contribute(target, contributor, 1, [], [first], ownPool: false);
        tombstone.OwnerObservations.Should().Equal(first);
        var baselineKey = new NormalizationResourceKey(
            "relisten",
            "preexisting_owner_bindings",
            ordinary.SubjectUuid.ToString("D")
        );
        (await fixture.Work.GetInputContributionAsync(target, baselineKey))!
            .OwnerObservationSubjectUuids.Should()
            .Equal(first.SubjectUuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailureOrCancellationRollsBackObservationWatermarksAndEpochsThenSameWorkRetries(
        bool cancel
    )
    {
        var observed = await Observe((await Seed()).SubjectUuid);
        var ticket = await Begin(Subject(), [observed]);
        var before = await Pool();
        using var cancellation = new CancellationTokenSource();
        var failed = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (c, t, ct) =>
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "SELECT normalization.bump_candidate_universes(ARRAY['people'],ARRAY['00000000-0000-0000-0000-000000000000']::uuid[])",
                            transaction: t
                        )
                    );
                    if (cancel)
                    {
                        cancellation.Cancel();
                        return;
                    }
                    throw new InvalidOperationException("Abort after a semantic change");
                },
                cancellation.Token,
                candidateWriteScopes: [NormalizationCandidateUniverseKey.People]
            );
        if (cancel)
        {
            await failed.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await failed.Should().ThrowAsync<InvalidOperationException>();
        }
        (await Pool()).Should().Be(before);
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT published_desired_generation IS NULL AND published_editorial_revision IS NULL FROM normalization.work_owner_observations WHERE subject_uuid=@SubjectUuid",
                    ticket
                )
            )
        )
            .Should()
            .BeTrue();
        await Publish(ticket);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task ReciprocalFactObservationAndCausalDependencySettleAcrossFreshNoOpAttempts()
    {
        var person = await Seed();
        var recording = await Seed([person.SubjectUuid]);
        var personSubject = await SubjectOf(person);
        var observation = await Observe(recording.SubjectUuid);
        var observer = await Begin(personSubject, [observation]);
        await Publish(observer);
        (await Pending(recording.SubjectUuid)).Should().BeTrue();
        for (var pass = 0; pass < 2; pass++)
        {
            var recordingRefresh = await Begin(
                await SubjectOf(recording),
                [],
                [person.SubjectUuid],
                consumePool: false
            );
            await Publish(recordingRefresh);
            (await Pending(person.SubjectUuid)).Should().BeFalse();
            (await Pending(recording.SubjectUuid)).Should().BeFalse();
            if (pass == 0)
            {
                observer = await Begin(personSubject, [await Observe(recording.SubjectUuid)]);
                await Publish(observer);
            }
        }
    }

    [Test]
    public async Task OmittedOrEmptyObservationsKeepOldTicketAndRequestReplayShapes()
    {
        var subject = Subject();
        var operation = Guid.NewGuid();
        var ticket = await fixture.Work.BeginWorkAsync(
            operation,
            subject,
            "observation-v1",
            [],
            []
        );
        var replay = await fixture.Work.BeginWorkAsync(
            operation,
            subject,
            "observation-v1",
            [],
            [],
            expectedOwnerObservations: []
        );
        replay.WorkUuid.Should().Be(ticket.WorkUuid);
        var json = JsonSerializer.Serialize(ticket);
        json.Should().NotContain("OwnerObservations");
        var contributed = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            Subject(),
            "observation-v1",
            new(Contributor(), 0, [], [])
        );
        JsonSerializer.Serialize(contributed).Should().NotContain("OwnerObservations");
    }

    private Task<NormalizationSubjectObservation> Observe(Guid subjectUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<NormalizationSubjectObservation>(
                "SELECT uuid AS \"SubjectUuid\",desired_generation AS \"DesiredGeneration\",editorial_revision AS \"EditorialRevision\",published_work_uuid AS \"PublishedWorkUuid\" FROM normalization.work_subjects WHERE uuid=@subjectUuid",
                new { subjectUuid }
            )
        );

    private Task<NormalizationCandidateUniverseDependency> Pool() =>
        fixture.Db.WithWriteConnection(async c => new NormalizationCandidateUniverseDependency(
            NormalizationCandidateUniverseKey.People,
            await c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'"
            )
        ));

    private async Task<NormalizationWorkTicket> Seed(Guid[]? dependencies = null)
    {
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            Subject(),
            "observation-v1",
            [],
            dependencies ?? []
        );
        await Publish(ticket);
        return ticket;
    }

    private async Task<NormalizationWorkTicket> Begin(
        NormalizationSubjectKey subject,
        NormalizationSubjectObservation[] observations,
        Guid[]? dependencies = null,
        NormalizationResourceKey[]? resources = null,
        NormalizationCandidateUniverseDependency? pool = null,
        bool consumePool = true
    )
    {
        var state = await fixture.Work.GetSubjectAsync(subject);
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "observation-v1",
            resources ?? [],
            dependencies ?? [],
            state?.DesiredGeneration,
            candidateUniverses: consumePool ? [pool ?? await Pool()] : [],
            expectedOwnerObservations: observations
        );
    }

    private async Task<NormalizationWorkTicket> Contribute(
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        long revision,
        Guid[] ownedObservations,
        NormalizationSubjectObservation[] observations,
        bool ownPool = true
    ) =>
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            subject,
            "observation-v1",
            new(contributor, revision, [], [], ownedObservations),
            candidateUniverses: ownPool ? [await Pool()] : [],
            expectedOwnerObservations: observations
        );

    private async Task Change(NormalizationWorkTicket owner, string field)
    {
        var subject = await SubjectOf(owner);
        var state = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;
        if (field == "editorial")
        {
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    state.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(new { sameFacts = true }),
                    "Reviewed without accepted row changes",
                    "fixture-operator"
                ),
                NoWrite
            );
            return;
        }
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            field == "desired" ? "changed-policy" : state.PolicyVersion,
            [],
            [],
            state.DesiredGeneration
        );
        await Publish(ticket);
    }

    private Task<bool> HasEdge(Guid owner, Guid dependency) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.work_dependencies WHERE subject_uuid=@owner AND dependency_uuid=@dependency)",
                new { owner, dependency }
            )
        );

    private async Task Publish(NormalizationWorkTicket ticket) =>
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

    private static Task NoWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        Task.CompletedTask;

    private static Task NeverWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        throw new AssertionException("Stale or replayed work invoked the callback.");

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("fixture", "observation-contributor", Guid.NewGuid().ToString("D"));

    private async Task<NormalizationSubjectKey> SubjectOf(NormalizationWorkTicket ticket) =>
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!.Subject;

    private async Task<bool> Pending(Guid subject)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(row => row.SubjectUuid == subject))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
    }
}
