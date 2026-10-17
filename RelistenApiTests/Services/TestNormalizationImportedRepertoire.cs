using System.Text.Json;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationImportedRepertoire
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationImportedCatalogService service = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        service = new(fixture.Db, evidence);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "CREATE TABLE normalization.repertoire_capture_test_values(uuid uuid PRIMARY KEY)"
            )
        );
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RefreshRetainsStoredSongIdentitiesUnknownWordingAndOriginalClock()
    {
        var artist = await Artist();
        var ordered = new[] { Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(x => x.ToString("N"), StringComparer.Ordinal)
            .ToArray();
        var low = ordered[0];
        var high = ordered[1];
        await Song(artist, high, "Shared title", "stored-slug", "unqualified-provider-key");
        await Song(artist, low, "", "", "");
        var snapshot = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        snapshot.Should().NotBeNull();
        snapshot!.Capture.Revision.Resource.Should().Be(Resource(artist.Uuid));
        snapshot.Capture.Revision.CaptureVersion.Should().Be("legacy-artist-repertoire-v1");
        snapshot.Capture.Fetch.AcceptedAsCurrent.Should().BeTrue();
        using var json = JsonDocument.Parse(snapshot.Capture.Revision.Payload);
        var root = json.RootElement;
        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        root.GetProperty("artist_uuid").GetString().Should().Be(artist.Uuid.ToString("D"));
        var songs = root.GetProperty("songs").EnumerateArray().ToArray();
        songs.Select(x => x.GetProperty("uuid").GetGuid()).Should().Equal(low, high);
        songs[0].GetProperty("name").GetString().Should().BeEmpty();
        songs[0].GetProperty("slug").GetString().Should().BeEmpty();
        songs[0].GetProperty("upstream_identifier").GetString().Should().BeEmpty();
        songs[1]
            .GetProperty("upstream_identifier")
            .GetString()
            .Should()
            .Be("unqualified-provider-key");
        root.TryGetProperty("snapshot_at", out _).Should().BeFalse();
        var clock = root.GetProperty("catalog_inputs").EnumerateArray().Single();
        snapshot
            .CatalogInputs.Should()
            .Equal(
                new NormalizationCatalogDependency(
                    clock.GetProperty("artist_uuid").GetGuid(),
                    clock.GetProperty("generation").GetInt64()
                )
            );
        clock.GetProperty("generation").GetInt64().Should().Be(await Generation(artist.Uuid));
    }

    [Test]
    public async Task MissingArtistAndKnownEmptyDictionaryAreDifferentOutcomes()
    {
        var missing = Guid.NewGuid();
        (await service.RefreshArtistRepertoireAsync(missing)).Should().BeNull();
        (await evidence.GetCurrentInputAsync(Resource(missing))).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(Resource(missing))).Should().BeEmpty();
        var artist = await Artist();
        var snapshot = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        using var json = JsonDocument.Parse(snapshot!.Capture.Revision.Payload);
        json.RootElement.GetProperty("songs").GetArrayLength().Should().Be(0);
        snapshot
            .CatalogInputs.Should()
            .Equal(new NormalizationCatalogDependency(artist.Uuid, await Generation(artist.Uuid)));
    }

    [Test]
    public async Task EqualTitlesAcrossArtistsRetainSeparateResourcesAndStoredIds()
    {
        var a = await Artist();
        var b = await Artist();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Song(a, first, "Same title");
        await Song(b, second, "Same title");
        var firstSnapshot = await service.RefreshArtistRepertoireAsync(a.Uuid);
        var secondSnapshot = await service.RefreshArtistRepertoireAsync(b.Uuid);
        firstSnapshot!
            .Capture.Revision.Resource.Should()
            .NotBe(secondSnapshot!.Capture.Revision.Resource);
        Songs(firstSnapshot).Should().Equal(first);
        Songs(secondSnapshot).Should().Equal(second);
    }

    [Test]
    public async Task EqualFactsAndFencesReuseBytesWhileChangedCatalogFenceCreatesARevision()
    {
        var artist = await Artist();
        await Song(artist, Guid.NewGuid(), "Unchanged song");
        var first = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE setlist_songs SET updated_at=updated_at+interval '1 hour' WHERE artist_id=@Id",
                artist
            )
        );
        var second = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        second!.Capture.Revision.Uuid.Should().Be(first!.Capture.Revision.Uuid);
        second.Capture.Revision.Payload.Should().Equal(first.Capture.Revision.Payload);
        second
            .Capture.Fetch.ResourceGeneration.Should()
            .Be(first.Capture.Fetch.ResourceGeneration + 1);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE artists SET name=name||' revised' WHERE id=@Id", artist)
        );
        var changed = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        changed!.Capture.Revision.Uuid.Should().NotBe(first.Capture.Revision.Uuid);
        Songs(changed).Should().Equal(Songs(first));
        changed
            .CatalogInputs.Single()
            .Generation.Should()
            .BeGreaterThan(first.CatalogInputs.Single().Generation);
    }

    [Test]
    public async Task SongMovesInsertionsAndRemovalsRefreshCompleteMembership()
    {
        var a = await Artist();
        var b = await Artist();
        var song = Guid.NewGuid();
        await Song(a, song, "Moved song");
        var first = await service.RefreshArtistRepertoireAsync(a.Uuid);
        var emptyB = await service.RefreshArtistRepertoireAsync(b.Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE setlist_songs SET artist_id=@artist WHERE uuid=@song",
                new { artist = b.Id, song }
            )
        );
        var movedA = await service.RefreshArtistRepertoireAsync(a.Uuid);
        var movedB = await service.RefreshArtistRepertoireAsync(b.Uuid);
        Songs(movedA!).Should().BeEmpty();
        Songs(movedB!).Should().Equal(song);
        movedA!.Capture.Revision.Uuid.Should().NotBe(first!.Capture.Revision.Uuid);
        movedB!.Capture.Revision.Uuid.Should().NotBe(emptyB!.Capture.Revision.Uuid);
        var added = Guid.NewGuid();
        await Song(a, added, "Added song");
        Songs((await service.RefreshArtistRepertoireAsync(a.Uuid))!).Should().Equal(added);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM setlist_songs WHERE uuid=@added", new { added })
        );
        Songs((await service.RefreshArtistRepertoireAsync(a.Uuid))!).Should().BeEmpty();
    }

    [Test]
    public async Task OversizedDictionaryIsRejectedBeforeCaptureAndExactSongBoundIsComplete()
    {
        var artist = await Artist();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at)
                SELECT @Id,'Song '||n,n::text,n::text,gen_random_uuid(),now() FROM generate_series(1,50001) n
                """,
                artist,
                commandTimeout: 120
            )
        );
        var oversized = () => service.RefreshArtistRepertoireAsync(artist.Uuid);
        await oversized.Should().ThrowAsync<InvalidDataException>();
        (await evidence.GetCurrentInputAsync(Resource(artist.Uuid))).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(Resource(artist.Uuid))).Should().BeEmpty();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM setlist_songs WHERE id=(SELECT max(id) FROM setlist_songs WHERE artist_id=@Id)",
                artist
            )
        );
        var complete = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        Songs(complete!).Should().HaveCount(50000);
    }

    [Test]
    public async Task EscapedPayloadByteLimitDoesNotAdvanceExistingEvidence()
    {
        var artist = await Artist();
        var song = Guid.NewGuid();
        await Song(artist, song, "Known song");
        var previous = await service.RefreshArtistRepertoireAsync(artist.Uuid);
        // Control characters expand to six bytes in JSON; a raw-text length check is insufficient.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE setlist_songs SET name=repeat(chr(1),3000000) WHERE uuid=@song",
                new { song }
            )
        );
        var oversized = () => service.RefreshArtistRepertoireAsync(artist.Uuid);
        await oversized.Should().ThrowAsync<InvalidDataException>();
        var current = await evidence.GetCurrentInputAsync(Resource(artist.Uuid));
        current!.Revision.Uuid.Should().Be(previous!.Capture.Revision.Uuid);
        current.Generation.Should().Be(previous.Capture.Fetch.ResourceGeneration);
        (await evidence.GetFetchHistoryAsync(Resource(artist.Uuid))).Should().ContainSingle();
    }

    [Test]
    public async Task ConcurrentRefreshesKeepOneCatalogSnapshotAndRejectTheLosingGeneration()
    {
        var artist = await Artist();
        var originalSong = Guid.NewGuid();
        await Song(artist, originalSong, "Concurrent song");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<bool> Attempt()
        {
            try
            {
                await service.RefreshArtistRepertoireAsync(artist.Uuid, timeout.Token);
                return true;
            }
            catch (NormalizationStaleInputException)
            {
                return false;
            }
        }
        var attempts = await fixture.Db.WithWriteConnection(async c =>
        {
            await using var barrier = await ((NpgsqlConnection)c).BeginTransactionAsync(
                timeout.Token
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "LOCK TABLE setlist_songs IN ACCESS EXCLUSIVE MODE",
                    transaction: barrier,
                    cancellationToken: timeout.Token
                )
            );
            var running = new[] { Attempt(), Attempt() };
            var committed = false;
            try
            {
                // Both producers must have read generation zero before either can finish its
                // catalog projection. Observe real PostgreSQL lock waits instead of timing a sleep.
                await WaitForRepertoireReads(2, timeout.Token);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at) VALUES(@Id,'Later song',@key,@key,@uuid,now())",
                        new
                        {
                            artist.Id,
                            key = Guid.NewGuid().ToString(),
                            uuid = Guid.NewGuid(),
                        },
                        barrier,
                        cancellationToken: timeout.Token
                    )
                );
                await barrier.CommitAsync(timeout.Token);
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await barrier.RollbackAsync(CancellationToken.None);
                }
            }
            return await Task.WhenAll(running);
        });
        attempts.Count(x => x).Should().Be(1);
        var history = await evidence.GetFetchHistoryAsync(Resource(artist.Uuid));
        history.Should().HaveCount(2);
        history.Count(x => x.AcceptedAsCurrent).Should().Be(1);
        history.Select(x => x.InputRevisionUuid).Distinct().Should().ContainSingle();
        var current = (await evidence.GetCurrentInputAsync(Resource(artist.Uuid)))!;
        current.Generation.Should().Be(1);
        using var retained = JsonDocument.Parse(current.Revision.Payload);
        retained
            .RootElement.GetProperty("songs")
            .EnumerateArray()
            .Select(x => x.GetProperty("uuid").GetGuid())
            .Should()
            .Equal(originalSong);
        retained
            .RootElement.GetProperty("catalog_inputs")[0]
            .GetProperty("generation")
            .GetInt64()
            .Should()
            .BeLessThan(await Generation(artist.Uuid));
    }

    [Test]
    public async Task CancellationAndAmbientTransactionsDoNotStartCapture()
    {
        var artist = await Artist();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => service.RefreshArtistRepertoireAsync(artist.Uuid, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var invalid = () => service.RefreshArtistRepertoireAsync(artist.Uuid);
            await invalid.Should().ThrowAsync<InvalidOperationException>();
        }
        (await evidence.GetCurrentInputAsync(Resource(artist.Uuid))).Should().BeNull();
    }

    [Test]
    public async Task RolledBackPublicationRetainsTheCompletedSnapshotAndItsOriginalFence()
    {
        var artist = await Artist();
        await Song(artist, Guid.NewGuid(), "Retained song");
        var snapshot = (await service.RefreshArtistRepertoireAsync(artist.Uuid))!;
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "artist_repertoire", artist.Uuid),
            "test",
            [Resource(artist.Uuid)],
            [],
            catalogInputs: snapshot.CatalogInputs
        );
        var value = Guid.NewGuid();
        var failed = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (c, t, ct) =>
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT INTO normalization.repertoire_capture_test_values(uuid) VALUES(@value)",
                            new { value },
                            t,
                            cancellationToken: ct
                        )
                    );
                    throw new InvalidOperationException("Downstream publication failed.");
                }
            );
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.repertoire_capture_test_values WHERE uuid=@value)",
                    new { value }
                )
            )
        )
            .Should()
            .BeFalse();
        var current = await evidence.GetCurrentInputAsync(Resource(artist.Uuid));
        current!.Revision.Uuid.Should().Be(snapshot.Capture.Revision.Uuid);
        current.Revision.Payload.Should().Equal(snapshot.Capture.Revision.Payload);
        (await evidence.GetInputAsync(snapshot.Capture.Revision.Uuid)).Should().NotBeNull();
    }

    private async Task WaitForRepertoireReads(int count, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var blocked = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    new CommandDefinition(
                        "SELECT count(*)::integer FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE '%artist_repertoire_snapshot_count%'",
                        cancellationToken: ct
                    )
                )
            );
            if (blocked >= count)
            {
                return;
            }
            await Task.Delay(20, ct);
        }
    }

    private Task<CatalogArtist> Artist() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CatalogArtist>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Repertoire fixture',@slug,@uuid,now()) RETURNING id AS \"Id\",uuid AS \"Uuid\"",
                new { slug = Guid.NewGuid().ToString(), uuid = Guid.NewGuid() }
            )
        );

    private Task Song(
        CatalogArtist artist,
        Guid uuid,
        string name,
        string? slug = null,
        string? upstream = null
    ) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at) VALUES(@Id,@name,@slug,@upstream,@uuid,now())",
                new
                {
                    artist.Id,
                    name,
                    slug = slug ?? uuid.ToString("D"),
                    upstream = upstream ?? uuid.ToString("D"),
                    uuid,
                }
            )
        );

    private Task<long> Generation(Guid artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@artist",
                new { artist }
            )
        );

    private static Guid[] Songs(NormalizationArtistRepertoireSnapshot snapshot)
    {
        using var json = JsonDocument.Parse(snapshot.Capture.Revision.Payload);
        return json
            .RootElement.GetProperty("songs")
            .EnumerateArray()
            .Select(x => x.GetProperty("uuid").GetGuid())
            .ToArray();
    }

    private static NormalizationResourceKey Resource(Guid artist) =>
        new("legacy_catalog", "artist_repertoire", artist.ToString("D"));

    private sealed record CatalogArtist(int Id, Guid Uuid);
}
