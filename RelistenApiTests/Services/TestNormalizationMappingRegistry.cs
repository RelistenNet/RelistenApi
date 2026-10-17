using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// A negative provider-mapping lookup is still an input. These tests exercise mapping DML,
/// admission, publication and contributor ownership through the work service's public contract.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMappingRegistry
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task MappingArrivalInvalidatesNegativeLookupAndMakesPublishedOwnerPending()
    {
        var provider = await Provider();
        var artist = await Artist();
        var subject = Subject();
        var ticket = await Begin(subject, await Registry());
        ticket.SourceIdentities.Should().BeNull();
        await Publish(ticket);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();

        await Map(artist, provider);

        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        var refreshed = await Begin(subject, await Registry());
        await Publish(refreshed);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task MappingUnderAnotherArtistInvalidatesUniqueOwnerLookup()
    {
        var provider = await Provider();
        var firstArtist = await Artist();
        var secondArtist = await Artist();
        await Map(firstArtist, provider);
        var ticket = await Begin(Subject(), await Registry());

        await Map(secondArtist, provider);

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task ProviderRenameFencesLookupWithoutAnyRecordingOrArtistMapping()
    {
        var provider = await Provider();
        var ticket = await Begin(Subject(), await Registry());
        await Execute(
            "UPDATE upstream_sources SET name=@name WHERE id=@provider",
            new { provider, name = "renamed-registry-" + Guid.NewGuid() }
        );

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [TestCase("insert")]
    [TestCase("delete")]
    [TestCase("uuid")]
    [TestCase("musicbrainz_id")]
    public async Task ArtistIdentityChangesInvalidateLookupAdmissionPublicationAndPendingWork(
        string change
    )
    {
        var artist = await Artist();
        var snapshot = await Registry();
        var published = await Begin(Subject(), snapshot);
        await Publish(published);
        var unpublished = await Begin(Subject(), snapshot);

        switch (change)
        {
            case "insert":
                await Artist();
                break;
            case "delete":
                await Execute("DELETE FROM artists WHERE id=@artist", new { artist });
                break;
            case "uuid":
                await Execute(
                    "UPDATE artists SET uuid=@uuid WHERE id=@artist",
                    new { artist, uuid = Guid.NewGuid() }
                );
                break;
            case "musicbrainz_id":
                await Execute(
                    "UPDATE artists SET musicbrainz_id=@mbid WHERE id=@artist",
                    new { artist, mbid = Guid.NewGuid().ToString("D") }
                );
                break;
        }

        (await Pending(published.SubjectUuid)).Should().BeTrue();
        (await fixture.Work.TryPublishAsync(unpublished.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var staleAdmission = () => Begin(Subject(), snapshot);
        await staleAdmission.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ArtistNamesTimestampsAndUnchangedIdentifiersDoNotInvalidateMappingLookup()
    {
        var artist = await Artist();
        var snapshot = await Registry();
        var ticket = await Begin(Subject(), snapshot);

        await Execute(
            "UPDATE artists SET name=@name,updated_at=clock_timestamp(),uuid=uuid,musicbrainz_id=musicbrainz_id WHERE id=@artist",
            new { artist, name = "Renamed registry artist" }
        );

        (await Registry()).Should().Be(snapshot);
        await Publish(ticket);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task RolledBackMusicBrainzCorrectionDoesNotDirtyPublishedLookup()
    {
        var artist = await Artist();
        var snapshot = await Registry();
        var ticket = await Begin(Subject(), snapshot);
        await Publish(ticket);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "UPDATE artists SET musicbrainz_id=@mbid WHERE id=@artist",
            new { artist, mbid = Guid.NewGuid().ToString("D") },
            transaction
        );
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_mapping_registry_revision",
                transaction: transaction
            )
        )
            .Should()
            .BeGreaterThan(snapshot.Generation);

        await transaction.RollbackAsync();

        (await Registry()).Should().Be(snapshot);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task MappingRollbackAndNoOpDoNotInvalidateLookup()
    {
        var provider = await Provider();
        var artist = await Artist();
        await Map(artist, provider);
        var snapshot = await Registry();
        var ticket = await Begin(Subject(), snapshot);
        await Execute(
            "UPDATE artists_upstream_sources SET upstream_identifier=upstream_identifier WHERE artist_id=@artist AND upstream_source_id=@provider",
            new { artist, provider }
        );
        (await Registry()).Should().Be(snapshot);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM artists_upstream_sources WHERE artist_id=@artist AND upstream_source_id=@provider",
            new { artist, provider },
            transaction
        );
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_mapping_registry_revision",
                transaction: transaction
            )
        )
            .Should()
            .BeGreaterThan(snapshot.Generation);
        await transaction.RollbackAsync();

        (await Registry()).Should().Be(snapshot);
        await Publish(ticket);
    }

    [Test]
    public async Task StaleAdmissionRollsBackContributionAndRetryKeepsExactReplayContract()
    {
        var subject = Subject();
        var contributor = Contributor();
        var before = await Registry();
        await Provider();
        var workUuid = Guid.NewGuid();
        var stale = () => Contribute(subject, contributor, 0, before, workUuid);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        var current = await Registry();
        var ticket = await Contribute(subject, contributor, 0, current, workUuid);
        await Provider();

        (await Contribute(subject, contributor, 0, current, workUuid))
            .Should()
            .BeEquivalentTo(ticket);
        var reusedWithNewSnapshot = () => Contribute(subject, contributor, 0, before, workUuid);
        await reusedWithNewSnapshot.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task RunPersistedBeforeRegistrySupportStillReplaysThroughContributedEntrypoint()
    {
        var subject = new NormalizationSubjectKey(
            "people",
            "person",
            Guid.Parse("44178bb9-d880-48d9-9e30-c6da2b5dd630")
        );
        var contributor = new NormalizationResourceKey(
            "test",
            "registry",
            "legacy-registry-replay"
        );
        var workUuid = Guid.NewGuid();
        var original = await Contribute(subject, contributor, 0, null, workUuid);
        // Frozen pre-registry request hash and ticket shape simulate an existing durable run.
        // Recomputing the expected hash with today's record types would hide a compatibility break.
        await Execute(
            "UPDATE normalization.work_runs SET request_hash=@hash,ticket=ticket-'MappingRegistry' WHERE uuid=@workUuid",
            new
            {
                workUuid,
                hash = "49a20449c9468cd28850989da076721f3c83ef689abb7a46c467221528e97dc5",
            }
        );
        var replay = await Contribute(subject, contributor, 0, null, workUuid);
        replay.Should().BeEquivalentTo(original);
        (await fixture.Work.GetInputContributionAsync(subject, contributor))!
            .Revision.Should()
            .Be(1);
        await Publish(replay);
    }

    [Test]
    public async Task RegistryMembershipChangesNeedConfigurationCasButNewGenerationDoesNot()
    {
        var subject = Subject();
        var initial = await Begin(subject, null);
        // Adding a dependency changes the owner's configuration; refreshing its clock does not.
        var registry = await Registry();
        var withoutCas = () => Begin(subject, registry);
        await withoutCas.Should().ThrowAsync<NormalizationConflictException>();
        var added = await Begin(subject, registry, initial.DesiredGeneration);
        await Provider();
        var refreshed = await Begin(subject, await Registry());
        refreshed.DesiredGeneration.Should().Be(added.DesiredGeneration);
        var removeWithoutCas = () => Begin(subject, null);
        await removeWithoutCas.Should().ThrowAsync<NormalizationConflictException>();
        var removed = await Begin(subject, null, refreshed.DesiredGeneration);
        removed.MappingRegistry.Should().BeNull();
        await Publish(removed);
    }

    [Test]
    public async Task ContributorsRetainRegistryUntilLastMembershipIsRemoved()
    {
        var subject = Subject();
        var a = Contributor();
        var b = Contributor();
        await Contribute(subject, a, 0, await Registry());
        await Contribute(subject, b, 0, await Registry());
        var omitUnion = () => Begin(subject, null);
        await omitUnion.Should().ThrowAsync<NormalizationConflictException>();
        var retained = await Contribute(subject, a, 1, null);
        retained.MappingRegistry.Should().NotBeNull();
        var aState = await fixture.Work.GetInputContributionAsync(subject, a);
        aState!.Revision.Should().Be(2);
        aState.UsesMappingRegistry.Should().BeFalse();
        var removed = await Contribute(subject, b, 1, null);
        removed.MappingRegistry.Should().BeNull();
        await Publish(removed);
        await Provider();
        (await Pending(removed.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task AdoptingRegistryOnlyOwnerPreservesBaselineOwnership()
    {
        var subject = Subject();
        var previous = await Begin(subject, await Registry());
        var ticket = await Contribute(subject, Contributor(), 0, null);
        ticket.MappingRegistry.Should().NotBeNull();
        var baseline = await fixture.Work.GetInputContributionAsync(
            subject,
            new("relisten", "preexisting_owner_bindings", previous.SubjectUuid.ToString("D"))
        );
        baseline!.UsesMappingRegistry.Should().BeTrue();
        await Publish(ticket);
    }

    [Test]
    public async Task CancellationWhileWaitingForRegistryLeavesNoContributionAndAllowsExactRetry()
    {
        var subject = Subject();
        var contributor = Contributor();
        var registry = await Registry();
        var workUuid = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT generation FROM normalization.catalog_mapping_registry_revision FOR UPDATE",
            transaction: transaction
        );
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Work.BeginContributedWorkAsync(
            workUuid,
            subject,
            "registry-v1",
            new(contributor, 0, [], []),
            cancellationToken: cancellation.Token,
            mappingRegistry: registry
        );
        var waiting = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!waiting && DateTime.UtcNow < deadline)
        {
            waiting = await observer.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @pid=ANY(pg_blocking_pids(pid)) AND query LIKE '%catalog_mapping_registry_revision%')",
                new { pid = blocker.ProcessID }
            );
            if (!waiting)
            {
                await Task.Delay(10);
            }
        }
        waiting
            .Should()
            .BeTrue("the request must reach the server-side registry lock before cancellation");
        cancellation.Cancel();
        var cancelled = () => pending;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await transaction.RollbackAsync();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        var retried = await Contribute(subject, contributor, 0, registry, workUuid);
        await Publish(retried);
    }

    [Test]
    public async Task FailedPublicationRollsBackDomainWriteAndRegistryWatermarkBeforeRetry()
    {
        var ticket = await Begin(Subject(), await Registry());
        var personUuid = Guid.NewGuid();
        Task Write(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct
        ) =>
            connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@personUuid,'Registry callback','registry callback','person')",
                    new { personUuid },
                    transaction,
                    cancellationToken: ct
                )
            );
        var failing = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (connection, transaction, ct) =>
                {
                    await Write(connection, transaction, ct);
                    throw new InvalidOperationException("Test domain write failed after insert.");
                }
            );
        await failing.Should().ThrowAsync<InvalidOperationException>();
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
                    "SELECT published_generation FROM normalization.work_mapping_registry_inputs WHERE subject_uuid=@SubjectUuid",
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
    }

    [TestCase("DELETE FROM normalization.catalog_mapping_registry_revision")]
    [TestCase("TRUNCATE normalization.catalog_mapping_registry_revision")]
    public async Task RegistryClockCannotBeRemoved(string sql)
    {
        var before = await Registry();
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var remove = () => connection.ExecuteAsync(sql);
        await remove
            .Should()
            .ThrowAsync<PostgresException>()
            .WithMessage("*Retain the normalization mapping registry revision*");
        (await Registry()).Should().Be(before);
    }

    private Task<NormalizationWorkTicket> Begin(
        NormalizationSubjectKey subject,
        NormalizationMappingRegistryDependency? registry,
        long? expectedConfigurationGeneration = null
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "registry-v1",
            [],
            [],
            expectedConfigurationGeneration,
            mappingRegistry: registry
        );

    private Task<NormalizationWorkTicket> Contribute(
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        long revision,
        NormalizationMappingRegistryDependency? registry,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginContributedWorkAsync(
            workUuid ?? Guid.NewGuid(),
            subject,
            "registry-v1",
            new(contributor, revision, [], []),
            mappingRegistry: registry
        );

    private async Task Publish(NormalizationWorkTicket ticket) =>
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

    private async Task<bool> Pending(Guid uuid)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(row => row.SubjectUuid == uuid))
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

    private Task<NormalizationMappingRegistryDependency> Registry() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<NormalizationMappingRegistryDependency>(
                "SELECT generation AS \"Generation\" FROM normalization.catalog_mapping_registry_revision WHERE singleton"
            )
        );

    private Task<int> Provider() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "INSERT INTO upstream_sources(id,name) SELECT GREATEST(COALESCE(max(id),0)+1,1000),@name FROM upstream_sources RETURNING id",
                new { name = "registry-" + Guid.NewGuid() }
            )
        );

    private Task<int> Artist() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "INSERT INTO artists(uuid,name,slug,updated_at) VALUES(gen_random_uuid(),@name,@name,now()) RETURNING id",
                new { name = "registry-" + Guid.NewGuid() }
            )
        );

    private Task Map(int artist, int provider) =>
        Execute(
            "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@artist,@provider,'shared-registry-identifier')",
            new { artist, provider }
        );

    private Task Execute(string sql, object? parameters = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, parameters));

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("test", "registry", Guid.NewGuid().ToString("D"));

    private static Task NoWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        Task.CompletedTask;

    private static Task NeverWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        throw new AssertionException("Stale work must not publish domain changes.");
}
