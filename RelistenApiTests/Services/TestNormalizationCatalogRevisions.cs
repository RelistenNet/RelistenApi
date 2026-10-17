using Dapper;
using FluentAssertions;
using Npgsql;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises durable catalog clocks through catalog DML. Counters fence ownership and ABA
/// changes; their exact increments are deliberately not an importer transaction count.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCatalogRevisions
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task MeaningfulSourceChangesFenceOnlyTheirArtistWhileStatsRefreshDoesNot()
    {
        var a = await Source();
        var b = await Source();
        var before = await ArtistGeneration(a.ArtistUuid);
        var unrelated = await ArtistGeneration(b.ArtistUuid);
        var identity = await IdentityGeneration(a.UpstreamIdentifier);

        await Execute(
            """
            UPDATE sources SET updated_at=clock_timestamp(),created_at=created_at-interval '1 second',
                avg_rating=9,num_reviews=3,num_ratings=8,avg_rating_weighted=7 WHERE id=@SourceId
            """,
            a
        );

        (await ArtistGeneration(a.ArtistUuid)).Should().Be(before);
        (await IdentityGeneration(a.UpstreamIdentifier)).Should().Be(identity);
        await Execute(
            "UPDATE sources SET description='Corrected recording notes' WHERE id=@SourceId",
            a
        );
        (await ArtistGeneration(a.ArtistUuid)).Should().BeGreaterThan(before);
        (await IdentityGeneration(a.UpstreamIdentifier)).Should().BeGreaterThan(identity);
        (await ArtistGeneration(b.ArtistUuid)).Should().Be(unrelated);
    }

    [Test]
    public async Task SetAndFileChangesFenceParentOwnershipIncludingTrackAvailability()
    {
        var source = await Source();
        var previous = await ArtistGeneration(source.ArtistUuid);
        foreach (
            var sql in new[]
            {
                "UPDATE source_sets SET name='Encore',is_encore=true WHERE id=@SetId",
                "UPDATE source_tracks SET title='Corrected title',track_position=2 WHERE uuid=@TrackUuid",
                "UPDATE source_tracks SET mp3_url='https://example.test/corrected.mp3' WHERE uuid=@TrackUuid",
                "UPDATE source_tracks SET is_orphaned=true WHERE uuid=@TrackUuid",
                "DELETE FROM source_sets WHERE id=@SetId",
            }
        )
        {
            await Execute(sql, source);
            var current = await ArtistGeneration(source.ArtistUuid);
            current.Should().BeGreaterThan(previous);
            previous = current;
        }
    }

    [Test]
    public async Task RolledBackCatalogChangesDoNotConsumeRevisions()
    {
        var source = await Source();
        var before = await ArtistGeneration(source.ArtistUuid);
        var identity = await IdentityGeneration(source.UpstreamIdentifier);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "UPDATE sources SET description='Uncommitted' WHERE id=@SourceId",
            source,
            transaction
        );
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@ArtistUuid",
                source,
                transaction
            )
        )
            .Should()
            .BeGreaterThan(before);
        await transaction.RollbackAsync();

        (await ArtistGeneration(source.ArtistUuid)).Should().Be(before);
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().Be(identity);
    }

    [Test]
    public async Task CrossArtistDuplicateArrivalAndRemovalFenceTheGlobalIdentifier()
    {
        var a = await Source();
        var b = await Source();
        var artistA = await ArtistGeneration(a.ArtistUuid);
        var artistB = await ArtistGeneration(b.ArtistUuid);
        var identity = await IdentityGeneration(a.UpstreamIdentifier);
        var oldIdentity = await IdentityGeneration(b.UpstreamIdentifier);

        await Execute(
            "UPDATE sources SET upstream_identifier=@key WHERE id=@id",
            new { key = a.UpstreamIdentifier, id = b.SourceId }
        );

        (await ArtistGeneration(a.ArtistUuid)).Should().Be(artistA);
        (await ArtistGeneration(b.ArtistUuid)).Should().BeGreaterThan(artistB);
        (await IdentityGeneration(b.UpstreamIdentifier)).Should().BeGreaterThan(oldIdentity);
        var duplicate = await IdentityGeneration(a.UpstreamIdentifier);
        duplicate.Should().BeGreaterThan(identity);
        await Execute("DELETE FROM sources WHERE id=@SourceId", b);
        (await IdentityGeneration(a.UpstreamIdentifier)).Should().BeGreaterThan(duplicate);
        (await ArtistGeneration(a.ArtistUuid)).Should().Be(artistA);
    }

    [Test]
    public async Task ReparentingAwayAndBackCannotRestoreAnOldArtistOrIdentityClock()
    {
        var a = await Source();
        var b = await Source();
        var artistA = await ArtistGeneration(a.ArtistUuid);
        var artistB = await ArtistGeneration(b.ArtistUuid);
        var identity = await IdentityGeneration(a.UpstreamIdentifier);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "UPDATE sources SET artist_id=@ArtistId WHERE id=@SourceId",
            new { b.ArtistId, a.SourceId },
            transaction
        );
        await connection.ExecuteAsync(
            "UPDATE sources SET artist_id=@ArtistId WHERE id=@SourceId",
            a,
            transaction
        );
        await transaction.CommitAsync();

        (await ArtistGeneration(a.ArtistUuid)).Should().BeGreaterThan(artistA);
        (await ArtistGeneration(b.ArtistUuid)).Should().BeGreaterThan(artistB);
        (await IdentityGeneration(a.UpstreamIdentifier)).Should().BeGreaterThan(identity);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>("SELECT artist_id FROM sources WHERE id=@SourceId", a)
            )
        )
            .Should()
            .Be(a.ArtistId);
    }

    [Test]
    public async Task SetReparentingFencesBothSourceOwners()
    {
        var a = await Source();
        var b = await Source();
        var beforeA = await ArtistGeneration(a.ArtistUuid);
        var beforeB = await ArtistGeneration(b.ArtistUuid);
        await Execute(
            """
            UPDATE source_sets SET source_id=@target,index=10 WHERE id=@set;
            UPDATE source_sets SET source_id=@original,index=0 WHERE id=@set
            """,
            new
            {
                target = b.SourceId,
                original = a.SourceId,
                set = a.SetId,
            }
        );
        (await ArtistGeneration(a.ArtistUuid)).Should().BeGreaterThan(beforeA);
        (await ArtistGeneration(b.ArtistUuid)).Should().BeGreaterThan(beforeB);
    }

    [TestCase("song_membership")]
    [TestCase("source_assignment")]
    [TestCase("setlist_assignment")]
    [TestCase("performance_key")]
    public async Task MembershipAndPerformanceChangesFenceAnOtherwiseUnchangedArtist(
        string relationship
    )
    {
        var source = await Source();
        var account = await Accounts(source);
        var before = await ArtistGeneration(source.ArtistUuid);
        var insert = relationship switch
        {
            "song_membership" =>
                "INSERT INTO setlist_songs_plays(played_setlist_show_id,played_setlist_song_id) VALUES(@Setlist,@Song)",
            "source_assignment" =>
                "INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date) VALUES(@SourceId,@Show,'accepted','2000-01-01')",
            "setlist_assignment" =>
                "INSERT INTO setlist_show_assignments(setlist_show_id,show_id) VALUES(@Setlist,@Show)",
            "performance_key" =>
                "INSERT INTO show_performance_keys(artist_id,performance_key,show_id) VALUES(@ArtistId,'clock-test',@Show)",
            _ => throw new ArgumentOutOfRangeException(nameof(relationship)),
        };
        await Execute(insert, account);
        var after = await ArtistGeneration(source.ArtistUuid);
        after.Should().BeGreaterThan(before);
        var delete = relationship switch
        {
            "song_membership" =>
                "DELETE FROM setlist_songs_plays WHERE played_setlist_show_id=@Setlist AND played_setlist_song_id=@Song",
            "source_assignment" => "DELETE FROM source_show_assignments WHERE source_id=@SourceId",
            "setlist_assignment" =>
                "DELETE FROM setlist_show_assignments WHERE setlist_show_id=@Setlist",
            "performance_key" => "DELETE FROM show_performance_keys WHERE artist_id=@ArtistId",
            _ => throw new ArgumentOutOfRangeException(nameof(relationship)),
        };
        await Execute(delete, account);
        (await ArtistGeneration(source.ArtistUuid)).Should().BeGreaterThan(after);
    }

    [Test]
    public async Task LinkQualificationMappingAndProviderNameChangesFenceIdentityLookups()
    {
        var source = await Source();
        var unrelated = await Source();
        var provider = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                """
                INSERT INTO upstream_sources(id,name)
                SELECT GREATEST(COALESCE(max(id),0)+1,1000),@name FROM upstream_sources RETURNING id
                """,
                new { name = "catalog-clock-provider-" + Guid.NewGuid() }
            )
        );
        var values = new
        {
            source.SourceId,
            source.ArtistId,
            source.UpstreamIdentifier,
            provider,
        };
        var before = await IdentityGeneration(source.UpstreamIdentifier);
        await Execute(
            """
            INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
                VALUES(@SourceId,@provider,false,false,false,'https://example.test/source','Provider');
            INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@UpstreamIdentifier)
            """,
            values
        );
        var inserted = await IdentityGeneration(source.UpstreamIdentifier);
        inserted.Should().BeGreaterThan(before);
        await Execute("UPDATE links SET for_source=true WHERE source_id=@SourceId", values);
        var qualified = await IdentityGeneration(source.UpstreamIdentifier);
        qualified.Should().BeGreaterThan(inserted);
        var otherArtist = await ArtistGeneration(unrelated.ArtistUuid);
        var beforeRename = await ArtistGeneration(source.ArtistUuid);
        await Execute(
            "UPDATE upstream_sources SET name=name||'-corrected' WHERE id=@provider",
            values
        );
        (await ArtistGeneration(source.ArtistUuid)).Should().BeGreaterThan(beforeRename);
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().BeGreaterThan(qualified);
        (await ArtistGeneration(unrelated.ArtistUuid)).Should().Be(otherArtist);
    }

    [Test]
    public async Task RetainedArchiveResolutionAndIntakeChangesFenceTheKnownItem()
    {
        var source = await Source();
        var values = new
        {
            item = source.UpstreamIdentifier,
            source.SourceUuid,
            source.ArtistUuid,
        };
        var before = await IdentityGeneration(source.UpstreamIdentifier);
        await Execute(
            """
            INSERT INTO normalization.archive_item_resolutions(item_identifier,canonical_source_uuid,selected_artist_uuid,status)
                VALUES(@item,@SourceUuid,@ArtistUuid,'resolved');
            INSERT INTO normalization.archive_item_intake(item_identifier) VALUES(@item)
            """,
            values
        );
        var current = await IdentityGeneration(source.UpstreamIdentifier);
        current.Should().BeGreaterThan(before);
        await Execute(
            "UPDATE normalization.archive_item_resolutions SET last_attempt_at=now(),last_error='retry' WHERE item_identifier=@item",
            values
        );
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().Be(current);
        await Execute(
            "UPDATE normalization.archive_item_resolutions SET status='conflict' WHERE item_identifier=@item",
            values
        );
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().BeGreaterThan(current);
        current = await IdentityGeneration(source.UpstreamIdentifier);
        await Execute(
            "UPDATE normalization.archive_item_intake SET status='paused' WHERE item_identifier=@item",
            values
        );
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().BeGreaterThan(current);
    }

    [Test]
    public async Task ArtistDeletionAndRecreationRetainClockHistory()
    {
        var source = await Source();
        var before = await ArtistGeneration(source.ArtistUuid);
        var identity = await IdentityGeneration(source.UpstreamIdentifier);
        await Execute("DELETE FROM artists WHERE id=@ArtistId", source);
        var deleted = await ArtistGeneration(source.ArtistUuid);
        deleted.Should().BeGreaterThan(before);
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().BeGreaterThan(identity);

        await Execute(
            "INSERT INTO artists(uuid,name,slug,updated_at) VALUES(@ArtistUuid,'Recreated',@slug,now())",
            new { source.ArtistUuid, slug = Guid.NewGuid().ToString() }
        );
        (await ArtistGeneration(source.ArtistUuid)).Should().BeGreaterThan(deleted);
    }

    [Test]
    public async Task ArtistCascadeWaitsForArtistClockBeforeTakingItsSourceIdentityClock()
    {
        var source = await Source();
        await using var publication = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await using var deletion = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await publication.OpenAsync();
        await deletion.OpenAsync();
        await observer.OpenAsync();
        await using var transaction = await publication.BeginTransactionAsync();
        await publication.ExecuteScalarAsync<long>(
            "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@ArtistUuid FOR UPDATE",
            source,
            transaction
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var deleting = deletion.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM artists WHERE id=@ArtistId",
                source,
                cancellationToken: timeout.Token
            )
        );
        try
        {
            var waiting = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!waiting && !deleting.IsCompleted && DateTime.UtcNow < deadline)
            {
                waiting = await observer.ExecuteScalarAsync<bool>(
                    "SELECT @publisher=ANY(pg_blocking_pids(@deleter))",
                    new { publisher = publication.ProcessID, deleter = deletion.ProcessID }
                );
                if (!waiting)
                {
                    await Task.Delay(10);
                }
            }
            waiting.Should().BeTrue("artist deletion must wait on publication's artist clock");
            // Publication's second lock must remain free. Taking the item clock first in the
            // cascading writer would deadlock this publication against the artist deletion.
            await publication.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@UpstreamIdentifier FOR UPDATE",
                    source,
                    transaction,
                    commandTimeout: 2
                )
            );
        }
        finally
        {
            await transaction.RollbackAsync();
            await deleting;
        }
    }

    [Test]
    public async Task OfflineTruncateFencesRetainedClocksAndRollbackRestoresThem()
    {
        var source = await Source();
        var before = await ArtistGeneration(source.ArtistUuid);
        var identity = await IdentityGeneration(source.UpstreamIdentifier);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("TRUNCATE artists CASCADE", transaction: transaction);
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@ArtistUuid",
                source,
                transaction
            )
        )
            .Should()
            .BeGreaterThan(before);
        (
            await connection.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@UpstreamIdentifier",
                source,
                transaction
            )
        )
            .Should()
            .BeGreaterThan(identity);
        await transaction.RollbackAsync();
        (await ArtistGeneration(source.ArtistUuid)).Should().Be(before);
        (await IdentityGeneration(source.UpstreamIdentifier)).Should().Be(identity);
    }

    private async Task<CatalogRecording> Source()
    {
        var source = await fixture.Source();
        return await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CatalogRecording>(
                """
                SELECT s.id AS SourceId,s.uuid AS SourceUuid,a.id AS ArtistId,a.uuid AS ArtistUuid,s.upstream_identifier AS UpstreamIdentifier,
                    sets.id AS SetId,tr.uuid AS TrackUuid
                FROM sources s JOIN artists a ON a.id=s.artist_id JOIN source_sets sets ON sets.source_id=s.id
                JOIN source_tracks tr ON tr.source_id=s.id AND tr.source_set_id=sets.id WHERE s.id=@SourceId
                """,
                source
            )
        );
    }

    private async Task<CatalogAccount> Accounts(CatalogRecording source)
    {
        return await fixture.Db.WithWriteConnection(async c =>
        {
            var venue = await c.ExecuteScalarAsync<long>(
                "INSERT INTO venues(artist_id,name,uuid,updated_at) VALUES(@ArtistId,'Clock venue',@uuid,now()) RETURNING id",
                new { source.ArtistId, uuid = Guid.NewGuid() }
            );
            var show = await c.ExecuteScalarAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@ArtistId,'2000-01-01','2000-01-01',@uuid,now()) RETURNING id",
                new { source.ArtistId, uuid = Guid.NewGuid() }
            );
            var setlist = await c.ExecuteScalarAsync<long>(
                "INSERT INTO setlist_shows(artist_id,venue_id,upstream_identifier,date,uuid,updated_at) VALUES(@ArtistId,@venue,@key,'2000-01-01',@uuid,now()) RETURNING id",
                new
                {
                    source.ArtistId,
                    venue,
                    key = Guid.NewGuid().ToString(),
                    uuid = Guid.NewGuid(),
                }
            );
            var song = await c.ExecuteScalarAsync<long>(
                "INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at) VALUES(@ArtistId,'Song',@key,@key,@uuid,now()) RETURNING id",
                new
                {
                    source.ArtistId,
                    key = Guid.NewGuid().ToString(),
                    uuid = Guid.NewGuid(),
                }
            );
            return new CatalogAccount(source.SourceId, source.ArtistId, show, setlist, song);
        });
    }

    private Task Execute(string sql, object values) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, values));

    private Task<long> ArtistGeneration(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@uuid",
                new { uuid }
            )
        );

    private Task<long> IdentityGeneration(string key) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@key",
                new { key }
            )
        );

    private sealed record CatalogRecording(
        long SourceId,
        Guid SourceUuid,
        int ArtistId,
        Guid ArtistUuid,
        string UpstreamIdentifier,
        long SetId,
        Guid TrackUuid
    );

    private sealed record CatalogAccount(
        long SourceId,
        int ArtistId,
        long Show,
        long Setlist,
        long Song
    );
}
