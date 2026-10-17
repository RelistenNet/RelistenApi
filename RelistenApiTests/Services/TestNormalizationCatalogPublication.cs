using System.Diagnostics;
using System.Text;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCatalogPublication
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ShowReassignmentMakesWorkStaleAndFreshBindingCanRecoverOwnership()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        var showUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var showId = await c.ExecuteScalarAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@artist,'2000-01-01','2000-01-01 Late',@showUuid,now()) RETURNING id",
                new { artist = context.Source.ArtistId, showUuid }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET show_id=@showId WHERE id=@source",
                new { showId, source = context.Source.SourceId }
            );
        });
        var called = false;

        var stale = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );
        stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
        var replay = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) => throw new AssertionException("A stale replay invoked its callback.")
        );
        replay.Replayed.Should().BeTrue();
        replay.Status.Should().Be(NormalizationPublicationStatus.Stale);

        var refreshed = context with { Snapshot = await Bind(context.Observation) };
        refreshed.Snapshot.Recording.Value!.ShowUuid.Should().Be(showUuid);
        var replacement = await Begin(refreshed);
        (await fixture.Work.TryPublishAsync(replacement.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await IsPending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task BindingChangedBeforeBeginCannotBePromotedToTheNewRevision()
    {
        var context = await Prepare();
        await UpdateTaper(context.Source.SourceId, "New wording");

        var begin = () => Begin(context);
        await begin.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ChangeAndReversionStillInvalidateTheOldSnapshot()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        await UpdateTaper(context.Source.SourceId, "Temporary wording");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=NULL WHERE id=@id",
                new { id = context.Source.SourceId }
            )
        );

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task RolledBackEditAndUnrelatedArtistDoNotInvalidateWork()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        await using (var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(
                "UPDATE sources SET taper='Rolled back' WHERE id=@id",
                new { id = context.Source.SourceId },
                transaction
            );
            await transaction.RollbackAsync();
        }
        var unrelated = await fixture.Source();
        await UpdateTaper(unrelated.SourceId, "Another artist");

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task NewDuplicateUnderAnotherArtistInvalidatesGlobalArchiveBinding()
    {
        var (context, item, providerId) = await PrepareArchive();
        var ticket = await Begin(context);

        var duplicate = await fixture.Source();
        await QualifyArchiveSource(duplicate, item, providerId);

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await Bind(context.Observation))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Ambiguous);
    }

    [Test]
    public async Task CommittedCatalogChangeMakesAnAlreadyPublishedOwnerPending()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask);
        await UpdateTaper(context.Source.SourceId, "New credit");

        (await IsPending(ticket.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task PublicationSerializesBeforeAConcurrentCatalogWriter()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await writer.OpenAsync();
        Task? change = null;
        var publication = fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            async (_, _, ct) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            change = writer.ExecuteAsync(
                "UPDATE sources SET taper='Later writer' WHERE id=@id",
                new { id = context.Source.SourceId }
            );
            await RequireLockWait(writer.ProcessID);
            change.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(publication, change ?? Task.CompletedTask);
        }
        (await publication).Status.Should().Be(NormalizationPublicationStatus.Published);
        (await IsPending(ticket.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task PublicationFencesADuplicateArrivingUnderAnotherArtist()
    {
        var (context, item, providerId) = await PrepareArchive();
        var rival = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@item WHERE id=@id",
                new { item, id = rival.SourceId }
            )
        );
        // Rebind after preparing the unqualified rival. Its later source-link insertion is
        // the only catalog change concurrent with publication.
        context = context with
        {
            Snapshot = await Bind(context.Observation),
        };
        var ticket = await Begin(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await writer.OpenAsync();
        Task? change = null;
        var publication = fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            async (_, _, ct) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            change = writer.ExecuteAsync(
                "INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@id,@providerId,true,false,false,'https://archive.org','Archive')",
                new { id = rival.SourceId, providerId }
            );
            await RequireLockWait(writer.ProcessID);
            change.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(publication, change ?? Task.CompletedTask);
        }
        (await publication).Status.Should().Be(NormalizationPublicationStatus.Published);
        (await IsPending(ticket.SubjectUuid)).Should().BeTrue();
        (await Bind(context.Observation))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Ambiguous);
    }

    [Test]
    public async Task CancellationReleasesCatalogClocksAndLeavesTheRunRetryable()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await writer.OpenAsync();
        Task? change = null;
        var publication = fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            async (_, _, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
            cancellation.Token
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            change = writer.ExecuteAsync(
                "UPDATE sources SET taper='After cancellation' WHERE id=@id",
                new { id = context.Source.SourceId }
            );
            await RequireLockWait(writer.ProcessID);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await Task.WhenAll(publication, change ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
                // The callback is expected to stop with its transaction rolled back.
            }
        }
        var cancelled = () => publication;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        var retry = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) => throw new AssertionException("Changed catalog work must remain stale.")
        );
        retry.Status.Should().Be(NormalizationPublicationStatus.Stale);
        retry.Replayed.Should().BeFalse();
    }

    [Test]
    public async Task WriterCommitWhilePublicationWaitsRejectsItsOldReadSet()
    {
        var context = await Prepare();
        var ticket = await Begin(context);
        await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await writer.ExecuteAsync(
            "UPDATE sources SET taper='Earlier writer' WHERE id=@id",
            new { id = context.Source.SourceId },
            transaction
        );
        var called = false;
        var publication = fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );
        try
        {
            await RequireBlockedBy(writer.ProcessID);
        }
        finally
        {
            await transaction.CommitAsync();
            await publication;
        }
        (await publication).Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
    }

    private async Task<bool> IsPending(Guid subjectUuid)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(subject => subject.SubjectUuid == subjectUuid))
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

    private async Task<(Context Context, string Item, int ProviderId)> PrepareArchive()
    {
        var source = await fixture.Source();
        var item = "binding-publication-" + Guid.NewGuid().ToString("N");
        var providerId = await fixture.Db.WithWriteConnection(async c =>
        {
            return await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') RETURNING id"
                );
        });
        await QualifyArchiveSource(source, item, providerId);
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                new("archive.org", "item_metadata", item),
                Encoding.UTF8.GetBytes(
                    $$"""{"metadata":{"identifier":"{{item}}","taper":"Pat"},"files":[]}"""
                ),
                "application/json",
                "archive-item-response-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var observation = new NormalizationProviderDecoder()
            .Decode(capture.Revision)
            .Recordings.Single();
        var context = new Context(
            source,
            observation,
            capture.Revision.Resource,
            await Bind(observation)
        );
        context.Snapshot.SourceIdentities.Should().ContainSingle();
        return (context, item, providerId);
    }

    private async Task<Context> Prepare()
    {
        var source = await fixture.Source();
        var seed = await new NormalizationLegacyInputService(
            fixture.Db,
            new(fixture.Db)
        ).SeedRecordingAsync(source.SourceUuid);
        var observation = new NormalizationProviderDecoder()
            .Decode(seed.Input!.Revision)
            .Recordings.Single();
        return new(source, observation, seed.Input.Resource, await Bind(observation));
    }

    private Task<NormalizationRecordingBindingSnapshot> Bind(
        ProviderRecordingObservation observation
    ) => new NormalizationCatalogBindingService(fixture.Db).BindRecordingAsync(observation);

    private Task<NormalizationWorkTicket> Begin(Context context) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", context.Source.SourceUuid),
            "catalog-binding-test-v1",
            [context.Resource],
            [],
            catalogInputs: context.Snapshot.CatalogInputs,
            sourceIdentities: context.Snapshot.SourceIdentities,
            sourceUuids: context.Snapshot.SourceUuids
        );

    private Task UpdateTaper(int sourceId, string taper) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@taper WHERE id=@sourceId",
                new { sourceId, taper }
            )
        );

    private Task QualifyArchiveSource(CatalogSource source, string item, int providerId) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@item WHERE id=@id",
                new { item, id = source.SourceId }
            );
            await c.ExecuteAsync(
                "INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label,updated_at) VALUES(@id,@providerId,true,false,false,@url,'Archive',now())",
                new
                {
                    id = source.SourceId,
                    providerId,
                    url = "https://archive.org/details/" + item,
                }
            );
        });

    private Task RequireLockWait(int pid) =>
        RequireDatabaseCondition(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid AND wait_event_type='Lock')",
            pid
        );

    private Task RequireBlockedBy(int pid) =>
        RequireDatabaseCondition(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid)))",
            pid
        );

    private async Task RequireDatabaseCondition(string sql, int pid)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(sql, new { pid })
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The expected PostgreSQL lock wait was not observed.");
    }

    private sealed record Context(
        CatalogSource Source,
        ProviderRecordingObservation Observation,
        NormalizationResourceKey Resource,
        NormalizationRecordingBindingSnapshot Snapshot
    );
}
