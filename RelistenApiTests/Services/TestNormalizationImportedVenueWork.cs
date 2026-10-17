using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Imported venues have their own catalog clocks because an unowned venue has no artist clock.
/// Exercise freshness, contributor ownership and recovery through the public work contract.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationImportedVenueWork
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task UnownedVenueChangesRejectOldAdmissionAndInvalidatePublication()
    {
        var venue = await CreateVenue();
        var snapshot = await Snapshot(venue);
        var published = await Begin(Subject(), [snapshot]);
        await Publish(published);
        var running = await Begin(Subject(), [snapshot]);
        (await Pending(published.SubjectUuid)).Should().BeFalse();

        await Execute("UPDATE venues SET name='Corrected venue' WHERE uuid=@venue", new { venue });

        (await Pending(published.SubjectUuid)).Should().BeTrue();
        (await fixture.Work.TryPublishAsync(running.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var rejectedSubject = Subject();
        var stale = () => Begin(rejectedSubject, [snapshot]);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(rejectedSubject)).Should().BeNull();
        var refreshed = await Begin(Subject(), [await Snapshot(venue)]);
        await Publish(refreshed);
        (await Pending(refreshed.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task MissingVenueAdmissionRetainsZeroClockAndInsertionInvalidatesIt()
    {
        var venue = Guid.NewGuid();
        (await Snapshot(venue)).Generation.Should().Be(0);
        var ticket = await Begin(Subject(), [new(venue, 0)]);
        ticket
            .ImportedVenues.Should()
            .BeEquivalentTo(new[] { new NormalizationImportedVenueDependency(venue, 0) });
        await Publish(ticket);

        await CreateVenue(venue);

        (await Snapshot(venue)).Generation.Should().BeGreaterThan(0);
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        var stale = () => Begin(Subject(), [new(venue, 0)]);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task InsertionBeforeFirstAdmissionDoesNotFreshenMissingSnapshot()
    {
        var venue = Guid.NewGuid();
        var missing = await Snapshot(venue);
        await CreateVenue(venue);
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();

        var stale = () => Contribute(subject, contributor, 0, [missing], workUuid);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
        var retried = await Contribute(subject, contributor, 0, [await Snapshot(venue)], workUuid);
        await Publish(retried);
    }

    [Test]
    public async Task FailedMissingClockExpectationRollsBackInitializationBeforeExactRetry()
    {
        var venue = Guid.NewGuid();
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var rejected = () => Contribute(subject, contributor, 0, [new(venue, 1)], workUuid);
        await rejected.Should().ThrowAsync<NormalizationConflictException>();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue)",
                    new { venue }
                )
            )
        )
            .Should()
            .BeFalse();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();

        var retried = await Contribute(subject, contributor, 0, [new(venue, 0)], workUuid);
        await Publish(retried);
    }

    [Test]
    public async Task DeleteRecreateAndUuidReplacementRetainBothSidesOfVenueIdentity()
    {
        var originalUuid = await CreateVenue();
        var initial = await Snapshot(originalUuid);
        var original = await Begin(Subject(), [initial]);
        await Publish(original);
        await Execute("DELETE FROM venues WHERE uuid=@venue", new { venue = originalUuid });
        var deleted = await Snapshot(originalUuid);
        deleted.Generation.Should().BeGreaterThan(initial.Generation);
        (await Pending(original.SubjectUuid)).Should().BeTrue();
        var absent = await Begin(Subject(), [deleted]);

        await CreateVenue(originalUuid);
        var recreated = await Snapshot(originalUuid);
        recreated.Generation.Should().BeGreaterThan(deleted.Generation);
        (await fixture.Work.TryPublishAsync(absent.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var recreatedTicket = await Begin(Subject(), [recreated]);
        var replacementUuid = Guid.NewGuid();
        var missingReplacement = await Begin(Subject(), [new(replacementUuid, 0)]);

        await Execute(
            "UPDATE venues SET uuid=@replacementUuid WHERE uuid=@originalUuid",
            new { replacementUuid, originalUuid }
        );

        (await Snapshot(originalUuid)).Generation.Should().BeGreaterThan(recreated.Generation);
        (await Snapshot(replacementUuid)).Generation.Should().BeGreaterThan(0);
        (await fixture.Work.TryPublishAsync(recreatedTicket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await fixture.Work.TryPublishAsync(missingReplacement.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task NoOpAndRolledBackVenueCorrectionsDoNotDirtyPublishedWork()
    {
        var venue = await CreateVenue();
        var snapshot = await Snapshot(venue);
        var ticket = await Begin(Subject(), [snapshot]);
        await Publish(ticket);
        await Execute(
            "UPDATE venues SET name=name,updated_at=clock_timestamp() WHERE uuid=@venue",
            new { venue }
        );
        (await Snapshot(venue)).Should().Be(snapshot);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "UPDATE venues SET location='Corrected location' WHERE uuid=@venue",
            new { venue },
            transaction
        );
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue",
                new { venue },
                transaction
            )
        )
            .Should()
            .BeGreaterThan(snapshot.Generation);
        await transaction.RollbackAsync();

        (await Snapshot(venue)).Should().Be(snapshot);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task VenueMembershipChangesNeedConfigurationCasButClockRefreshDoesNot()
    {
        var subject = Subject();
        var initial = await Begin(subject, []);
        var venue = await CreateVenue();
        var snapshot = await Snapshot(venue);
        var withoutCas = () => Begin(subject, [snapshot]);
        await withoutCas.Should().ThrowAsync<NormalizationConflictException>();
        var added = await Begin(subject, [snapshot], initial.DesiredGeneration);
        await Execute(
            "UPDATE venues SET past_names='Earlier name' WHERE uuid=@venue",
            new { venue }
        );
        var refreshed = await Begin(subject, [await Snapshot(venue)]);
        refreshed.DesiredGeneration.Should().Be(added.DesiredGeneration);
        var removeWithoutCas = () => Begin(subject, []);
        await removeWithoutCas.Should().ThrowAsync<NormalizationConflictException>();
        var removed = await Begin(subject, [], refreshed.DesiredGeneration);
        removed.ImportedVenues.Should().BeNull();
        await Publish(removed);
        await Execute(
            "UPDATE venues SET location='Another location' WHERE uuid=@venue",
            new { venue }
        );
        (await Pending(removed.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task ContributionsRetainCompleteVenueUnionUntilEachOwnerWithdraws()
    {
        var first = await CreateVenue();
        var second = await CreateVenue();
        var subject = Subject();
        var a = Contributor();
        var b = Contributor();
        await Contribute(subject, a, 0, [await Snapshot(first)]);
        var union = await Contribute(subject, b, 0, [await Snapshot(second)]);
        union
            .ImportedVenues!.Select(value => value.VenueUuid)
            .Should()
            .BeEquivalentTo(new[] { first, second });
        var incomplete = async () => await Begin(subject, [await Snapshot(second)]);
        await incomplete.Should().ThrowAsync<NormalizationConflictException>();
        var retained = await Contribute(subject, a, 1, []);
        retained.ImportedVenues!.Select(value => value.VenueUuid).Should().Equal(second);
        var aState = await fixture.Work.GetInputContributionAsync(subject, a);
        aState!.Revision.Should().Be(2);
        aState.ImportedVenueUuids.Should().BeNullOrEmpty();
        var bState = await fixture.Work.GetInputContributionAsync(subject, b);
        bState!.ImportedVenueUuids.Should().Equal(second);
        await Publish(retained);
        await Execute(
            "UPDATE venues SET name='Withdrawn input changed' WHERE uuid=@venue",
            new { venue = first }
        );
        (await Pending(retained.SubjectUuid)).Should().BeFalse();
        await Execute(
            "UPDATE venues SET name='Retained input changed' WHERE uuid=@venue",
            new { venue = second }
        );
        (await Pending(retained.SubjectUuid)).Should().BeTrue();

        var removed = await Contribute(subject, b, 1, []);
        removed.ImportedVenues.Should().BeNull();
        await Publish(removed);
        await Execute(
            "UPDATE venues SET name='All inputs withdrawn' WHERE uuid=@venue",
            new { venue = second }
        );
        (await Pending(removed.SubjectUuid)).Should().BeFalse();
        var staleRevision = () => Contribute(subject, b, 1, []);
        await staleRevision.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task SharedVenueDependencySurvivesOneContributorWithdrawal()
    {
        var venue = await CreateVenue();
        var subject = Subject();
        var first = Contributor();
        var second = Contributor();
        await Contribute(subject, first, 0, [await Snapshot(venue)]);
        await Contribute(subject, second, 0, [await Snapshot(venue)]);
        var retained = await Contribute(subject, first, 1, []);
        retained.ImportedVenues!.Select(input => input.VenueUuid).Should().Equal(venue);
        await Publish(retained);

        await Execute(
            "UPDATE venues SET location='Shared dependency changed' WHERE uuid=@venue",
            new { venue }
        );

        (await Pending(retained.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task AdoptingVenueOnlyOwnerRetainsExplicitBaselineContribution()
    {
        var venue = await CreateVenue();
        var subject = Subject();
        var original = await Begin(subject, [await Snapshot(venue)]);
        var ticket = await Contribute(subject, Contributor(), 0, []);
        ticket.ImportedVenues!.Select(value => value.VenueUuid).Should().Equal(venue);
        var baseline = await fixture.Work.GetInputContributionAsync(
            subject,
            new("relisten", "preexisting_owner_bindings", original.SubjectUuid.ToString("D"))
        );
        baseline!.ImportedVenueUuids.Should().Equal(venue);
        await Publish(ticket);
    }

    [Test]
    public async Task ExactReplayReturnsOriginalVenueSnapshotAndChangedSnapshotConflicts()
    {
        var venue = await CreateVenue();
        var snapshot = await Snapshot(venue);
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var original = await Contribute(subject, contributor, 0, [snapshot], workUuid);
        await Execute(
            "UPDATE venues SET name='Replay correction' WHERE uuid=@venue",
            new { venue }
        );

        (await Contribute(subject, contributor, 0, [snapshot], workUuid))
            .Should()
            .BeEquivalentTo(original);
        var changed = async () =>
            await Contribute(subject, contributor, 0, [await Snapshot(venue)], workUuid);
        await changed.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor))!
            .Revision.Should()
            .Be(1);
        (await fixture.Work.TryPublishAsync(original.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await fixture.Work.TryPublishAsync(original.WorkUuid, NeverWrite))
            .Replayed.Should()
            .BeTrue();
    }

    [TestCase(false, "dad7512ca8315503c0304e798defcd3044291778b619fa70d6d05500b535a849")]
    [TestCase(true, "a62f4b028a129cf883fb7f28af3c8d90eea63960193067a6118439aa4b5c5bb5")]
    public async Task PreVenueRequestHashesAndTicketsStillReplay(
        bool contributed,
        string frozenHash
    )
    {
        var subject = new NormalizationSubjectKey(
            "venues",
            "venue",
            Guid.Parse("f7bb9829-7955-42dd-9123-0db9f1de65c2")
        );
        var contributor = new NormalizationResourceKey("test", "venue", "pre-venue-replay");
        var workUuid = Guid.NewGuid();
        Task<NormalizationWorkTicket> Start() =>
            contributed
                ? Contribute(subject, contributor, 0, [], workUuid)
                : fixture.Work.BeginWorkAsync(workUuid, subject, "venue-v1", [], []);
        var original = await Start();
        // Frozen canonical hashes use the pre-venue JSON shapes. Do not calculate these
        // from current model serialization: that would conceal an incompatible new field.
        await Execute(
            "UPDATE normalization.work_runs SET request_hash=@frozenHash,ticket=ticket-'ImportedVenues' WHERE uuid=@workUuid",
            new { frozenHash, workUuid }
        );

        (await Start()).Should().BeEquivalentTo(original);
        await Publish(original);
    }

    [Test]
    public async Task FailedPublicationRollsBackDomainRowsAndVenueWatermarkBeforeRetry()
    {
        var venue = await CreateVenue();
        var ticket = await Begin(Subject(), [await Snapshot(venue)]);
        var personUuid = Guid.NewGuid();
        Task Write(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct
        ) =>
            connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@personUuid,'Venue callback','venue callback','person')",
                    new { personUuid },
                    transaction,
                    cancellationToken: ct
                )
            );
        var failed = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (connection, transaction, ct) =>
                {
                    await Write(connection, transaction, ct);
                    throw new InvalidOperationException("Domain publication failed after insert.");
                }
            );
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@personUuid)",
                    new { personUuid }
                )
            )
        )
            .Should()
            .BeFalse();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<long?>(
                    "SELECT published_generation FROM normalization.work_venue_inputs WHERE subject_uuid=@SubjectUuid",
                    ticket
                )
            )
        )
            .Should()
            .BeNull();
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, Write))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@personUuid)",
                    new { personUuid }
                )
            )
        )
            .Should()
            .BeTrue();
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task CancellationAtVenueLockLeavesNoContributionAndExactRequestCanRetry()
    {
        var venue = await CreateVenue();
        var snapshot = await Snapshot(venue);
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue FOR UPDATE",
            new { venue },
            transaction
        );
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Work.BeginContributedWorkAsync(
            workUuid,
            subject,
            "venue-v1",
            new(contributor, 0, [], []),
            cancellationToken: cancellation.Token,
            importedVenues: [snapshot]
        );
        try
        {
            await WaitForVenueLock(blocker.ProcessID);
            cancellation.Cancel();
            var cancelled = () => pending;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException) { }
        }
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        var retried = await Contribute(subject, contributor, 0, [snapshot], workUuid);
        await Publish(retried);
    }

    private Task<NormalizationWorkTicket> Begin(
        NormalizationSubjectKey subject,
        IReadOnlyList<NormalizationImportedVenueDependency> venues,
        long? expectedConfigurationGeneration = null
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "venue-v1",
            [],
            [],
            expectedConfigurationGeneration,
            importedVenues: venues
        );

    private Task<NormalizationWorkTicket> Contribute(
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        long revision,
        IReadOnlyList<NormalizationImportedVenueDependency> venues,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginContributedWorkAsync(
            workUuid ?? Guid.NewGuid(),
            subject,
            "venue-v1",
            new(contributor, revision, [], []),
            importedVenues: venues
        );

    private Task<Guid> CreateVenue(Guid? uuid = null) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>(
                "INSERT INTO venues(uuid,name,location,updated_at) VALUES(@venue,'Imported venue fixture','Fixture location',clock_timestamp()) RETURNING uuid",
                new { venue = uuid ?? Guid.NewGuid() }
            )
        );

    private Task<NormalizationImportedVenueDependency> Snapshot(Guid venue) =>
        fixture.Db.WithWriteConnection(async c => new NormalizationImportedVenueDependency(
            venue,
            await c.ExecuteScalarAsync<long?>(
                "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue",
                new { venue }
            ) ?? 0
        ));

    private async Task Publish(NormalizationWorkTicket ticket) =>
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? after = null;
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(row => row.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
        throw new AssertionException("Pending work pagination did not terminate.");
    }

    private static async Task WaitForVenueLock(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await observer.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @blockerPid=ANY(pg_blocking_pids(pid)) AND query LIKE '%catalog_venue_revisions%')",
                    new { blockerPid }
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new AssertionException("Admission did not reach the imported venue lock.");
    }

    private Task Execute(string sql, object? parameters = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, parameters));

    private static NormalizationSubjectKey Subject() => new("venues", "venue", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("test", "venue", Guid.NewGuid().ToString("D"));

    private static Task NoWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        Task.CompletedTask;

    private static Task NeverWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        throw new AssertionException("Stale work must not publish domain rows.");
}
