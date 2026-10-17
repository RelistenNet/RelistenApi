using System.Text.Json;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Imported venue snapshots retain exact catalog facts and clocks, including artist-null unused venues.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationImportedVenueRefresh
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationImportedCatalogService service = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        service = new(fixture.Db, evidence);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task UnusedArtistNullVenueRetainsExactFactsAndItsOwnProducerClock()
    {
        var venue = await Venue();
        var snapshot = (await service.RefreshImportedVenueAsync(venue))!;
        snapshot.CatalogInputs.Should().BeEmpty();
        snapshot.ImportedVenues.Should().ContainSingle().Which.VenueUuid.Should().Be(venue);
        snapshot.Capture.Revision.Resource.Should().Be(Resource(venue));
        using var document = JsonDocument.Parse(snapshot.Capture.Revision.Payload);
        var root = document.RootElement;
        var facts = root.GetProperty("venue");
        facts.GetProperty("uuid").GetGuid().Should().Be(venue);
        facts.GetProperty("artist_uuid").ValueKind.Should().Be(JsonValueKind.Null);
        facts.GetProperty("name").GetString().Should().Be("Hall 🎤");
        facts.GetProperty("location").GetString().Should().Be("City, Region");
        facts
            .GetProperty("past_names")
            .GetString()
            .Should()
            .Be("Former Hall; wording remains prose");
        facts.GetProperty("latitude").GetDouble().Should().Be(40.5);
        facts.GetProperty("longitude").GetDouble().Should().Be(-73.2);
        root.GetProperty("imported_venues")[0]
            .GetProperty("generation")
            .GetInt64()
            .Should()
            .Be(snapshot.ImportedVenues.Single().Generation);
        root.GetProperty("catalog_inputs").GetArrayLength().Should().Be(0);
        facts.TryGetProperty("id", out _).Should().BeFalse();
        facts.TryGetProperty("updated_at", out _).Should().BeFalse();
    }

    [Test]
    public async Task OwnedVenueIncludesTheArtistFenceReadWithItsFacts()
    {
        var source = await fixture.Source();
        var venue = await Venue(source.ArtistId);
        var snapshot = (await service.RefreshImportedVenueAsync(venue))!;
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        snapshot.CatalogInputs.Should().ContainSingle().Which.ArtistUuid.Should().Be(artist);
        using var document = JsonDocument.Parse(snapshot.Capture.Revision.Payload);
        var storedClock = document.RootElement.GetProperty("catalog_inputs")[0];
        storedClock.GetProperty("artist_uuid").GetGuid().Should().Be(artist);
        storedClock
            .GetProperty("generation")
            .GetInt64()
            .Should()
            .Be(snapshot.CatalogInputs.Single().Generation);
    }

    [Test]
    public async Task OperationalTimestampChangesReuseTheSameFactsAndProducerFences()
    {
        var venue = await Venue();
        var first = (await service.RefreshImportedVenueAsync(venue))!;
        await Execute(
            "UPDATE venues SET updated_at=updated_at+interval '1 second' WHERE uuid=@venue",
            new { venue }
        );
        var repeated = (await service.RefreshImportedVenueAsync(venue))!;
        repeated.Capture.Revision.Uuid.Should().Be(first.Capture.Revision.Uuid);
        repeated.ImportedVenues.Should().Equal(first.ImportedVenues);
        repeated
            .Capture.Fetch.ResourceGeneration.Should()
            .BeGreaterThan(first.Capture.Fetch.ResourceGeneration);
    }

    [Test]
    public async Task FactChangesRetainTheOriginalFenceInsideEachImmutableRevision()
    {
        var venue = await Venue();
        var original = (await service.RefreshImportedVenueAsync(venue))!;
        await Execute("UPDATE venues SET name='Corrected Hall' WHERE uuid=@venue", new { venue });
        var changed = (await service.RefreshImportedVenueAsync(venue))!;
        changed.Capture.Revision.Uuid.Should().NotBe(original.Capture.Revision.Uuid);
        changed
            .ImportedVenues.Single()
            .Generation.Should()
            .BeGreaterThan(original.ImportedVenues.Single().Generation);
        var oldInput = (await evidence.GetInputAsync(original.Capture.Revision.Uuid))!;
        using var oldDocument = JsonDocument.Parse(oldInput.Payload);
        oldDocument
            .RootElement.GetProperty("venue")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Hall 🎤");
        oldDocument
            .RootElement.GetProperty("imported_venues")[0]
            .GetProperty("generation")
            .GetInt64()
            .Should()
            .Be(original.ImportedVenues.Single().Generation);
    }

    [Test]
    public async Task ChangedUuidAndDeletionRetainBothClockTombstones()
    {
        var oldUuid = await Venue();
        var before = await VenueGeneration(oldUuid);
        var newUuid = Guid.NewGuid();
        await Execute(
            "UPDATE venues SET uuid=@newUuid WHERE uuid=@oldUuid",
            new { oldUuid, newUuid }
        );
        (await VenueGeneration(oldUuid)).Should().BeGreaterThan(before);
        var renamed = await VenueGeneration(newUuid);
        await Execute("DELETE FROM venues WHERE uuid=@newUuid", new { newUuid });
        (await VenueGeneration(newUuid)).Should().BeGreaterThan(renamed);
        (await service.RefreshImportedVenueAsync(oldUuid)).Should().BeNull();
        (await service.RefreshImportedVenueAsync(newUuid)).Should().BeNull();
        (await evidence.GetCurrentInputAsync(Resource(oldUuid))).Should().BeNull();
    }

    [Test]
    public async Task RolledBackCatalogChangeDoesNotInvalidateRetainedFacts()
    {
        var venue = await Venue();
        var before = (await service.RefreshImportedVenueAsync(venue))!;
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE venues SET location='Uncommitted location' WHERE uuid=@venue",
                new { venue },
                transaction
            );
            await transaction.RollbackAsync();
        }
        (await VenueGeneration(venue)).Should().Be(before.ImportedVenues.Single().Generation);
        (await evidence.GetCurrentInputAsync(Resource(venue)))!
            .Revision.Payload.Should()
            .Equal(before.Capture.Revision.Payload);
        (await service.RefreshImportedVenueAsync(venue))!
            .Capture.Revision.Uuid.Should()
            .Be(before.Capture.Revision.Uuid);
    }

    [TestCase("DELETE FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue")]
    [TestCase("TRUNCATE normalization.catalog_venue_revisions CASCADE")]
    public async Task VenueClockCannotBeRemoved(string sql)
    {
        var venue = await Venue();
        var before = await VenueGeneration(venue);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var remove = () => connection.ExecuteAsync(sql, new { venue });
        await remove
            .Should()
            .ThrowAsync<PostgresException>()
            .WithMessage("*Retain imported venue revisions*");
        (await VenueGeneration(venue)).Should().Be(before);
    }

    [TestCase("venues")]
    [TestCase("artists")]
    public async Task OfflineCatalogTruncateAdvancesRetainedVenueClocks(string relation)
    {
        var venue = await Venue();
        var before = await VenueGeneration(venue);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync($"TRUNCATE {relation} CASCADE", transaction: transaction);
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue",
                new { venue },
                transaction
            )
        )
            .Should()
            .BeGreaterThan(before);
        await transaction.RollbackAsync();
        (await VenueGeneration(venue)).Should().Be(before);
    }

    [Test]
    public async Task ArtistDeleteWaitsForVenueReadersBeforeAcquiringTheMappingRegistry()
    {
        var source = await fixture.Source();
        var venue = await Venue(source.ArtistId);
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider = await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT GREATEST(COALESCE(max(id),0)+1,1000),@name,'https://example.test' FROM upstream_sources RETURNING id",
                new { name = "Venue cascade " + Guid.NewGuid() }
            );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,'artist')",
                new { source.ArtistId, provider }
            );
        });
        var before = await VenueGeneration(venue);
        await using var reader = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await reader.OpenAsync();
        await using var readerTransaction = await reader.BeginTransactionAsync();
        await reader.ExecuteAsync(
            "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue FOR SHARE",
            new { venue },
            readerTransaction
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var deletion = fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM artists WHERE id=@ArtistId",
                    source,
                    cancellationToken: timeout.Token
                )
            )
        );
        try
        {
            await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
            await observer.OpenAsync();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var waiting = false;
            while (!waiting && DateTime.UtcNow < deadline)
            {
                waiting = await observer.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @pid=ANY(pg_blocking_pids(pid)) AND query LIKE 'DELETE FROM artists%')",
                    new { pid = reader.ProcessID }
                );
                if (!waiting)
                {
                    await Task.Delay(10);
                }
            }
            waiting
                .Should()
                .BeTrue(
                    "artist deletion must reach the imported venue fence while a reader holds it"
                );
            await using var probeTransaction = await observer.BeginTransactionAsync();
            await observer.ExecuteAsync(
                "SET LOCAL lock_timeout='250ms'",
                transaction: probeTransaction
            );
            // A cascade which already acquired this later clock would create the
            // reverse dependency against publication's venue-before-registry order.
            await observer.ExecuteAsync(
                "SELECT generation FROM normalization.catalog_mapping_registry_revision FOR UPDATE",
                transaction: probeTransaction
            );
            await probeTransaction.RollbackAsync();
        }
        finally
        {
            await readerTransaction.RollbackAsync();
            await deletion;
        }
        (await VenueGeneration(venue)).Should().BeGreaterThan(before);
        (await service.RefreshImportedVenueAsync(venue)).Should().BeNull();
    }

    [Test]
    public async Task MissingCancelledAndAmbientRequestsDoNotRetainEvidence()
    {
        var missing = Guid.NewGuid();
        (await service.RefreshImportedVenueAsync(missing)).Should().BeNull();
        (await evidence.GetCurrentInputAsync(Resource(missing))).Should().BeNull();
        var venue = await Venue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => service.RefreshImportedVenueAsync(venue, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var ambient = () => service.RefreshImportedVenueAsync(venue);
            await ambient
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*outside the catalog transaction*");
        }
        (await evidence.GetCurrentInputAsync(Resource(venue))).Should().BeNull();
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public async Task NonfiniteCoordinatesCannotBecomeJsonEvidence(double latitude)
    {
        var venue = await Venue();
        await Execute(
            "UPDATE venues SET latitude=@latitude WHERE uuid=@venue",
            new { latitude, venue }
        );
        var refresh = () => service.RefreshImportedVenueAsync(venue);
        await refresh.Should().ThrowAsync<InvalidDataException>();
        (await evidence.GetCurrentInputAsync(Resource(venue))).Should().BeNull();
    }

    [Test]
    public async Task OversizedVenueFailsWithoutTruncationOrEvidence()
    {
        var venue = await Venue();
        await Execute(
            "UPDATE venues SET name=@name WHERE uuid=@venue",
            new { name = new string('x', 16 * 1024 * 1024), venue }
        );
        var refresh = () => service.RefreshImportedVenueAsync(venue);
        await refresh.Should().ThrowAsync<InvalidDataException>().WithMessage("*byte limit*");
        (await evidence.GetCurrentInputAsync(Resource(venue))).Should().BeNull();
    }

    private async Task<Guid> Venue(int? artistId = null)
    {
        var venue = Guid.NewGuid();
        await Execute(
            """
            INSERT INTO venues(uuid,artist_id,name,location,upstream_identifier,slug,past_names,latitude,longitude,updated_at)
            VALUES(@venue,@artistId,'Hall 🎤','City, Region',@identifier,'hall','Former Hall; wording remains prose',40.5,-73.2,now())
            """,
            new
            {
                venue,
                artistId,
                identifier = venue.ToString(),
            }
        );
        return venue;
    }

    private Task<long> VenueGeneration(Guid venue) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venue",
                new { venue }
            )
        );

    private Task Execute(string sql, object? parameters = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, parameters));

    private static NormalizationResourceKey Resource(Guid venue) =>
        new("legacy_catalog", "imported_venue", venue.ToString("D"));
}
