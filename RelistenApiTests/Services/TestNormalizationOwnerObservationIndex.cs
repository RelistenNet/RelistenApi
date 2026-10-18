using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Large factual owner read sets retain the public admission and publication contract.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationOwnerObservationIndex
{
    private const int ObservedOwnerCount = 6789;
    private const string Policy = "owner-observation-index-v1";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationSubjectKey seedSubject = null!;
    private Guid seedOwnerUuid;
    private Guid[] ownerUuids = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        fixture = new();
        seedSubject = Subject();
        var seed = await fixture.Work.BeginWorkAsync(Guid.NewGuid(), seedSubject, Policy, [], []);
        await Publish(seed);
        seedOwnerUuid = seed.SubjectUuid;
        var copies = Enumerable
            .Range(1, ObservedOwnerCount - 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        // These rows only expand the coordination read set. They make no domain publication
        // claim: cloned owners retain the schema's unpublished defaults and have no work run.
        // Every admission, stamp change and publication exercised below uses the public service.
        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO normalization.work_subjects
                    (uuid,domain,subject_kind,subject_uuid,desired_generation,editorial_revision,policy_version,configuration_hash)
                SELECT ids.uuid,s.domain,s.subject_kind,ids.uuid,s.desired_generation,s.editorial_revision,s.policy_version,s.configuration_hash
                FROM normalization.work_subjects s
                CROSS JOIN unnest(@copies::uuid[]) AS ids(uuid)
                WHERE s.uuid=@seedOwnerUuid
                """,
                new { copies, seedOwnerUuid }
            )
        );
        ownerUuids = copies.Append(seedOwnerUuid).ToArray();
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ThousandsOfOriginalObservationsNormalizePublishAndReplayWithoutRefreshingStamps()
    {
        var observations = await Observations();
        observations.Should().HaveCount(ObservedOwnerCount);
        var pool = await Pool();
        var target = Subject();
        var workUuid = Guid.NewGuid();
        var repeated = observations.Append(observations[0]).Append(observations[^1]).ToArray();

        var ticket = await Begin(workUuid, target, repeated, pool);

        ticket.OwnerObservations.Should().Equal(observations);
        ticket.Dependencies.Should().BeEmpty();
        await Publish(ticket);
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .Be(workUuid);

        await ChangeSeed("published");
        var replay = await Begin(workUuid, target, repeated.Reverse().ToArray(), pool);
        replay.OwnerObservations.Should().Equal(observations);
        var publicationReplay = await fixture.Work.TryPublishAsync(workUuid, NeverWrite);
        publicationReplay.Status.Should().Be(NormalizationPublicationStatus.Published);
        publicationReplay.Replayed.Should().BeTrue();
    }

    [Test]
    public async Task EveryChangedStampRejectsOriginalAdmissionAndPublicationWithoutCallingTheWriter()
    {
        foreach (var stamp in new[] { "desired", "editorial", "published" })
        {
            // Tests share only the large mechanical owner population. Each scenario reads a
            // fresh original snapshot and admits a separate observer before changing the seed.
            var observations = await Observations();
            var pool = await Pool();
            var ticket = await Begin(Guid.NewGuid(), Subject(), observations, pool);
            var before = observations.Single(observation =>
                observation.SubjectUuid == seedOwnerUuid
            );

            await ChangeSeed(stamp);

            var after = (await Observations()).Single(observation =>
                observation.SubjectUuid == seedOwnerUuid
            );
            switch (stamp)
            {
                case "desired":
                    after.DesiredGeneration.Should().BeGreaterThan(before.DesiredGeneration);
                    break;
                case "editorial":
                    after.EditorialRevision.Should().BeGreaterThan(before.EditorialRevision);
                    break;
                case "published":
                    (after.PublishedWorkUuid != before.PublishedWorkUuid).Should().BeTrue();
                    after.DesiredGeneration.Should().Be(before.DesiredGeneration);
                    after.EditorialRevision.Should().Be(before.EditorialRevision);
                    break;
            }
            var stale = await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite);
            stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
            stale.Replayed.Should().BeFalse();
            (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
                .Replayed.Should()
                .BeTrue();

            var unconfigured = Subject();
            var admission = () => Begin(Guid.NewGuid(), unconfigured, observations, pool);
            await admission
                .Should()
                .ThrowAsync<NormalizationConflictException>()
                .WithMessage("*Observed owners changed*");
            (await fixture.Work.GetSubjectAsync(unconfigured)).Should().BeNull();
        }
    }

    [Test]
    public async Task ConflictingDuplicateAndMissingOwnerCannotConfigureANewObserver()
    {
        var observations = await Observations();
        var pool = await Pool();
        var conflicting = observations
            .Append(
                observations[0] with
                {
                    DesiredGeneration = observations[0].DesiredGeneration + 1,
                }
            )
            .ToArray();
        var duplicateTarget = Subject();
        var duplicate = () => Begin(Guid.NewGuid(), duplicateTarget, conflicting, pool);

        await duplicate
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*one consistent*stamp*");
        (await fixture.Work.GetSubjectAsync(duplicateTarget)).Should().BeNull();

        var missing = observations.ToArray();
        missing[^1] = missing[^1] with { SubjectUuid = Guid.NewGuid() };
        var missingTarget = Subject();
        var missingOwner = () => Begin(Guid.NewGuid(), missingTarget, missing, pool);

        await missingOwner.Should().ThrowAsync<ArgumentException>();
        (await fixture.Work.GetSubjectAsync(missingTarget)).Should().BeNull();
    }

    private Task<NormalizationWorkTicket> Begin(
        Guid workUuid,
        NormalizationSubjectKey subject,
        NormalizationSubjectObservation[] observations,
        NormalizationCandidateUniverseDependency pool
    ) =>
        fixture.Work.BeginWorkAsync(
            workUuid,
            subject,
            Policy,
            [],
            [],
            candidateUniverses: [pool],
            expectedOwnerObservations: observations
        );

    private async Task ChangeSeed(string stamp)
    {
        var state = (await fixture.Work.GetSubjectAsync(seedOwnerUuid))!;
        if (stamp == "editorial")
        {
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    seedOwnerUuid,
                    state.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(new { sameFacts = true }),
                    "Exercise original owner editorial stamp",
                    "fixture-operator"
                ),
                NoWrite
            );
            return;
        }
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            seedSubject,
            stamp == "desired"
                ? "changed-owner-policy-" + Guid.NewGuid().ToString("D")
                : state.PolicyVersion,
            [],
            [],
            state.DesiredGeneration
        );
        if (stamp == "published")
        {
            await Publish(ticket);
        }
    }

    private async Task<NormalizationSubjectObservation[]> Observations()
    {
        var observations = await fixture.Db.WithWriteConnection(connection =>
            connection.QueryAsync<NormalizationSubjectObservation>(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",
                    editorial_revision AS "EditorialRevision",published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_subjects WHERE uuid=ANY(@ownerUuids)
                """,
                new { ownerUuids }
            )
        );
        return observations.OrderBy(observation => observation.SubjectUuid).ToArray();
    }

    private Task<NormalizationCandidateUniverseDependency> Pool() =>
        fixture.Db.WithWriteConnection(
            async connection => new NormalizationCandidateUniverseDependency(
                NormalizationCandidateUniverseKey.People,
                await connection.ExecuteScalarAsync<long>(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'"
                )
            )
        );

    private async Task Publish(NormalizationWorkTicket ticket)
    {
        var result = await fixture.Work.TryPublishAsync(ticket.WorkUuid, NoWrite);
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        result.Replayed.Should().BeFalse();
    }

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());

    private static Task NoWrite(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct
    ) => Task.CompletedTask;

    private static Task NeverWrite(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct
    ) =>
        throw new AssertionException(
            "Stale or replayed owner-observation work invoked the domain callback."
        );
}
