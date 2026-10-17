using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Exact lookup absence is durable work input, independent of a recording's accepted
/// extraction. These cases exercise original stamps through admission, publication and retry.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSourceUuidBindings
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task MissingUuidSettlesUntilArrivalAndOldObservationCannotPublishOrAdmit()
    {
        var uuid = Guid.NewGuid();
        var absent = await Snapshot(uuid);
        var settled = await Begin(Subject(), [absent]);
        var running = await Begin(Subject(), [absent]);
        await Publish(settled);
        (await Pending(settled.SubjectUuid)).Should().BeFalse();

        await Source(await Artist(), uuid);

        (await Pending(settled.SubjectUuid)).Should().BeTrue();
        (await fixture.Work.TryPublishAsync(running.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var rejected = Subject();
        await ((Func<Task>)(() => Begin(rejected, [absent])))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(rejected)).Should().BeNull();
        var refreshed = await Begin(Subject(), [await Snapshot(uuid)]);
        await Publish(refreshed);
        (await Pending(refreshed.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task ArrivalBeforeFirstAdmissionDoesNotReplaceOriginalZeroWithCurrentGeneration()
    {
        var uuid = Guid.NewGuid();
        var absent = await Snapshot(uuid);
        await Source(await Artist(), uuid);
        var subject = Subject();
        var contributor = Contributor();
        var attempt = Guid.NewGuid();

        await ((Func<Task>)(() => Contribute(subject, contributor, 0, [absent], attempt)))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        await Publish(await Contribute(subject, contributor, 0, [await Snapshot(uuid)], attempt));
    }

    [Test]
    public async Task MissingIdentifierClockAlsoSettlesAndDetectsArrivalUnderAnyArtist()
    {
        var identifier = "absent-identifier-" + Guid.NewGuid();
        var subject = Subject();
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "lookup-v1",
            [],
            [],
            sourceIdentities: [new(identifier, 0)]
        );
        await Publish(ticket);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();

        await Source(await Artist(), Guid.NewGuid(), identifier);

        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        var stale = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                Subject(),
                "lookup-v1",
                [],
                [],
                sourceIdentities: [new(identifier, 0)]
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task InvalidExpectedClockRollsBackTombstoneAndContributionBeforeRetry()
    {
        var uuid = Guid.NewGuid();
        var subject = Subject();
        var contributor = Contributor();
        var attempt = Guid.NewGuid();
        await ((Func<Task>)(() => Contribute(subject, contributor, 0, [new(uuid, 1)], attempt)))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await ClockExists(uuid)).Should().BeFalse();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        await Publish(await Contribute(subject, contributor, 0, [new(uuid, 0)], attempt));
    }

    [Test]
    public async Task DeleteRecreateAndUuidReplacementFenceBothHistoricalIdentities()
    {
        var artist = await Artist();
        var uuid = Guid.NewGuid();
        var source = await Source(artist, uuid);
        var before = await Snapshot(uuid);
        var running = await Begin(Subject(), [before]);
        await Execute("DELETE FROM sources WHERE id=@source", new { source });
        await Source(artist, uuid);
        (await Snapshot(uuid)).Generation.Should().BeGreaterThan(before.Generation);
        (await fixture.Work.TryPublishAsync(running.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);

        var replacement = Guid.NewGuid();
        var oldUuid = await Snapshot(uuid);
        var absentReplacement = await Snapshot(replacement);
        await Execute(
            "UPDATE sources SET uuid=@replacement WHERE uuid=@uuid",
            new { replacement, uuid }
        );
        foreach (var original in new[] { oldUuid, absentReplacement })
        {
            await ((Func<Task>)(() => Begin(Subject(), [original])))
                .Should()
                .ThrowAsync<NormalizationConflictException>();
        }
    }

    [Test]
    public async Task RolledBackCatalogChangePreservesAdmissionAndPublishedWatermark()
    {
        var uuid = Guid.NewGuid();
        await Source(await Artist(), uuid);
        var original = await Snapshot(uuid);
        var settled = await Begin(Subject(), [original]);
        await Publish(settled);
        await using var c = fixture.Db.CreateConnection(false, false);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync(
            "UPDATE sources SET taper='A correction' WHERE uuid=@uuid",
            new { uuid },
            tx
        );
        await tx.RollbackAsync();

        (await Snapshot(uuid)).Should().Be(original);
        (await Pending(settled.SubjectUuid)).Should().BeFalse();
        await Publish(await Begin(Subject(), [original]));
    }

    [TestCase("artist_uuid")]
    [TestCase("artist_delete")]
    [TestCase("source_owner")]
    [TestCase("provider_link")]
    [TestCase("wording")]
    public async Task ConsumedOwnershipQualificationAndWordingChangesInvalidateExactLookup(
        string change
    )
    {
        var artist = await Artist();
        var uuid = Guid.NewGuid();
        var source = await Source(artist, uuid);
        var original = await Snapshot(uuid);
        var running = await Begin(Subject(), [original]);
        switch (change)
        {
            case "artist_uuid":
                await Execute(
                    "UPDATE artists SET uuid=@replacement WHERE id=@artist",
                    new { replacement = Guid.NewGuid(), artist }
                );
                break;
            case "artist_delete":
                await Execute("DELETE FROM artists WHERE id=@artist", new { artist });
                break;
            case "source_owner":
                await Execute(
                    "UPDATE sources SET artist_id=@next WHERE id=@source",
                    new { next = await Artist(), source }
                );
                break;
            case "provider_link":
                await Execute(
                    "INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@source,1,true,false,false,@url,'Lookup fixture')",
                    new { source, url = "https://archive.org/details/" + uuid }
                );
                break;
            case "wording":
                await Execute(
                    "UPDATE sources SET taper='An updated taper' WHERE id=@source",
                    new { source }
                );
                break;
        }
        (await Snapshot(uuid)).Generation.Should().BeGreaterThan(original.Generation);
        (await fixture.Work.TryPublishAsync(running.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task OperationalTimestampDoesNotRequeueSettledLookup()
    {
        var uuid = Guid.NewGuid();
        await Source(await Artist(), uuid);
        var original = await Snapshot(uuid);
        var settled = await Begin(Subject(), [original]);
        await Publish(settled);
        await Execute(
            "UPDATE sources SET updated_at=updated_at+interval '1 minute' WHERE uuid=@uuid",
            new { uuid }
        );
        (await Snapshot(uuid)).Should().Be(original);
        (await Pending(settled.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task ContributionUnionRetainsSharedKeysAndRemovalCannotRestoreAnOldRequest()
    {
        var shared = Guid.NewGuid();
        var firstOnly = Guid.NewGuid();
        var secondOnly = Guid.NewGuid();
        var subject = Subject();
        var first = Contributor();
        var second = Contributor();
        await Contribute(subject, first, 0, [new(shared, 0), new(firstOnly, 0)]);
        var union = await Contribute(subject, second, 0, [new(shared, 0), new(secondOnly, 0)]);
        union
            .SourceUuids!.Select(input => input.SourceUuid)
            .Should()
            .BeEquivalentTo([shared, firstOnly, secondOnly]);
        var withoutFirst = await Contribute(subject, first, 1, []);
        withoutFirst
            .SourceUuids!.Select(input => input.SourceUuid)
            .Should()
            .BeEquivalentTo([shared, secondOnly]);
        (await fixture.Work.GetInputContributionAsync(subject, first))!
            .SourceUuids.Should()
            .BeNullOrEmpty();
        (await fixture.Work.GetInputContributionAsync(subject, second))!
            .SourceUuids.Should()
            .BeEquivalentTo([shared, secondOnly]);
        await ((Func<Task>)(() => Contribute(subject, first, 1, [new(firstOnly, 0)])))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        var partialOrdinary = () => Begin(subject, [new(shared, 0)]);
        await partialOrdinary.Should().ThrowAsync<NormalizationConflictException>();
        await Publish(withoutFirst);
        (await Pending(withoutFirst.SubjectUuid)).Should().BeFalse();
        await Source(await Artist(), shared);
        (await Pending(withoutFirst.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task AdoptionKeepsExistingUnmanagedSourceLookupBindingsAsBaseline()
    {
        var original = Guid.NewGuid();
        var incoming = Guid.NewGuid();
        var subject = Subject();
        var unmanaged = await Begin(subject, [new(original, 0)]);
        var adopted = await Contribute(subject, Contributor(), 0, [new(incoming, 0)]);
        adopted
            .SourceUuids!.Select(input => input.SourceUuid)
            .Should()
            .BeEquivalentTo([original, incoming]);
        var baseline = await fixture.Work.GetInputContributionAsync(
            subject,
            new("relisten", "preexisting_owner_bindings", unmanaged.SubjectUuid.ToString("D"))
        );
        baseline!.SourceUuids.Should().Equal(original);
    }

    [Test]
    public async Task ReplayUsesOriginalReadSetAndChangedOriginalStampIsARequestConflict()
    {
        var uuid = Guid.NewGuid();
        var subject = Subject();
        var workUuid = Guid.NewGuid();
        var original = await Begin(subject, [new(uuid, 0)], workUuid);
        await Publish(original);
        await Source(await Artist(), uuid);
        var replay = await Begin(subject, [new(uuid, 0)], workUuid);
        replay.Should().BeEquivalentTo(original);
        var current = await Snapshot(uuid);
        var changed = () => Begin(subject, [current], workUuid);
        await changed.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task PublicationFailureRollsBackLookupWatermarkAndCanBeRetried()
    {
        var uuid = Guid.NewGuid();
        var ticket = await Begin(Subject(), [new(uuid, 0)]);
        var failed = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                (_, _, _) => throw new InvalidOperationException("failed callback")
            );
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        await Publish(ticket);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task SourceArrivalWaitsForLookupPublicationThenRequeuesItsWatermark()
    {
        var uuid = Guid.NewGuid();
        var artist = await Artist();
        var ticket = await Begin(Subject(), [new(uuid, 0)]);
        var entered = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var publication = fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            async (c, t, ct) =>
            {
                entered.SetResult(
                    await c.ExecuteScalarAsync<int>(
                        new CommandDefinition(
                            "SELECT pg_backend_pid()",
                            transaction: t,
                            cancellationToken: ct
                        )
                    )
                );
                await release.Task.WaitAsync(ct);
            },
            timeout.Token
        );
        Task? insertion = null;
        try
        {
            var blocker = await entered.Task.WaitAsync(timeout.Token);
            insertion = fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@artist,@identifier,'2026-10-17',@uuid,now(),false,false,false)",
                        new
                        {
                            artist,
                            identifier = uuid.ToString(),
                            uuid,
                        },
                        cancellationToken: timeout.Token
                    )
                )
            );
            await WaitUntilBlocked(blocker, timeout.Token);
            insertion.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(publication, insertion ?? Task.CompletedTask);
        }

        (await publication).Status.Should().Be(NormalizationPublicationStatus.Published);
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        (await Snapshot(uuid)).Generation.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task CancellationWhileInitializingMissingClockLeavesNoPartialOwnerOrContribution()
    {
        var uuid = Guid.NewGuid();
        var subject = Subject();
        var contributor = Contributor();
        await using var gate = fixture.Db.CreateConnection(false, false);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        var blocker = await gate.ExecuteScalarAsync<int>(
            "SELECT pg_backend_pid()",
            transaction: tx
        );
        await gate.ExecuteAsync(
            "INSERT INTO normalization.catalog_source_uuid_revisions(source_uuid,generation) VALUES(@uuid,0)",
            new { uuid },
            tx
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            subject,
            "lookup-v1",
            new(contributor, 0, [], []),
            cancellationToken: cancellation.Token,
            sourceUuids: [new(uuid, 0)]
        );
        try
        {
            await WaitUntilBlocked(blocker, cancellation.Token);
            cancellation.Cancel();
            await ((Func<Task>)(async () => await pending))
                .Should()
                .ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            cancellation.Cancel();
            await tx.RollbackAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException) { }
        }
        (await ClockExists(uuid)).Should().BeFalse();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
    }

    private static NormalizationSubjectKey Subject() =>
        new("people", "lookup_fixture", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("test", "recording_lookup", Guid.NewGuid().ToString());

    private Task<NormalizationWorkTicket> Begin(
        NormalizationSubjectKey subject,
        NormalizationSourceUuidDependency[] inputs,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginWorkAsync(
            workUuid ?? Guid.NewGuid(),
            subject,
            "lookup-v1",
            [],
            [],
            sourceUuids: inputs
        );

    private Task<NormalizationWorkTicket> Contribute(
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        long revision,
        NormalizationSourceUuidDependency[] inputs,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginContributedWorkAsync(
            workUuid ?? Guid.NewGuid(),
            subject,
            "lookup-v1",
            new(contributor, revision, [], []),
            sourceUuids: inputs
        );

    private async Task Publish(NormalizationWorkTicket ticket) =>
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

    private static Task NeverWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        throw new AssertionException("A stale lookup must not run its publication callback.");

    private Task<NormalizationSourceUuidDependency> Snapshot(Guid uuid) =>
        fixture.Db.WithWriteConnection(async c => new NormalizationSourceUuidDependency(
            uuid,
            await c.QuerySingleOrDefaultAsync<long?>(
                "SELECT generation FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=@uuid",
                new { uuid }
            ) ?? 0
        ));

    private Task<bool> ClockExists(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=@uuid)",
                new { uuid }
            )
        );

    private Task<int> Artist() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Lookup test',@slug,@uuid,now()) RETURNING id",
                new { slug = Guid.NewGuid().ToString(), uuid = Guid.NewGuid() }
            )
        );

    private Task<int> Source(int artist, Guid uuid, string? identifier = null) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@artist,@identifier,'2026-10-17',@uuid,now(),false,false,false) RETURNING id",
                new
                {
                    artist,
                    identifier = identifier ?? uuid.ToString(),
                    uuid,
                }
            )
        );

    private Task Execute(string sql, object args) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, args));

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? after = null;
        for (var pageNumber = 0; pageNumber < 1000; pageNumber++)
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

    private async Task WaitUntilBlocked(int blocker, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var blocked = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @blocker=ANY(pg_blocking_pids(pid)))",
                    new { blocker }
                )
            );
            if (blocked)
            {
                return;
            }
            await Task.Delay(10, ct);
        }
    }
}
