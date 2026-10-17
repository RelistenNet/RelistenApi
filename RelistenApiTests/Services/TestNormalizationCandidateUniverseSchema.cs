using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Candidate clocks fence accepted-state and catalog-context changes at the database contract.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCandidateUniverseSchema
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SourceDeletionAndRecreationInvalidateContextEvenWithoutAPlayableFile()
    {
        var source = await fixture.Source();
        var before = await Epoch("people");
        await Sql("DELETE FROM sources WHERE id=@SourceId", source);
        var deleted = await Epoch("people");
        deleted.Should().BeGreaterThan(before);
        await Sql(
            """
            INSERT INTO sources(uuid,artist_id,upstream_identifier,display_date,updated_at,is_soundboard,is_remaster,has_jamcharts)
            VALUES(@SourceUuid,@ArtistId,@key,'1970-XX-XX',now(),false,false,false)
            """,
            new
            {
                source.SourceUuid,
                source.ArtistId,
                key = Guid.NewGuid().ToString(),
            }
        );
        (await Epoch("people")).Should().BeGreaterThan(deleted);
    }

    [Test]
    public async Task SourceUuidReplacementAndArtistUuidReplacementInvalidatePersonContext()
    {
        var source = await fixture.Source();
        var before = await Epoch("people");
        await Sql(
            "UPDATE sources SET uuid=@uuid WHERE id=@SourceId",
            new { uuid = Guid.NewGuid(), source.SourceId }
        );
        var moved = await Epoch("people");
        moved.Should().BeGreaterThan(before);
        await Sql(
            "UPDATE artists SET uuid=@uuid WHERE id=@ArtistId",
            new { uuid = Guid.NewGuid(), source.ArtistId }
        );
        (await Epoch("people")).Should().BeGreaterThan(moved);
    }

    [Test]
    public async Task UnchangedOwnershipAndOperationalCatalogUpdatesDoNotInvalidateContext()
    {
        var source = await fixture.Source();
        var before = await Epoch("people");
        await Sql(
            """
            UPDATE sources SET artist_id=artist_id,uuid=uuid,updated_at=now(),avg_rating=8,taper='Changed extraction input' WHERE id=@SourceId;
            UPDATE artists SET uuid=uuid,name=name||' renamed',updated_at=now() WHERE id=@ArtistId;
            """,
            source
        );
        (await Epoch("people")).Should().Be(before);
    }

    [Test]
    public async Task RolledBackAcceptedIdentityChangeDoesNotConsumeAGeneration()
    {
        var person = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@person,'Recorder','recorder','person')",
            new { person }
        );
        var people = await Epoch("people");
        var compositions = await Epoch("compositions");
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "UPDATE normalization.people SET display_name='Corrected recorder',name_key='corrected recorder' WHERE uuid=@person",
            new { person },
            transaction
        );
        (await Epoch(connection, transaction, "people")).Should().BeGreaterThan(people);
        (await Epoch(connection, transaction, "compositions")).Should().BeGreaterThan(compositions);
        await transaction.RollbackAsync();
        (await Epoch("people")).Should().Be(people);
        (await Epoch("compositions")).Should().Be(compositions);
    }

    [Test]
    public async Task TimestampOnlyIdentityUpdateDoesNotInvalidateCandidates()
    {
        var person = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@person,'Recorder','recorder','person')",
            new { person }
        );
        var before = await Epoch("people");
        var composition = await Epoch("compositions");
        await Sql(
            "UPDATE normalization.people SET updated_at=now() WHERE uuid=@person",
            new { person }
        );
        (await Epoch("people")).Should().Be(before);
        (await Epoch("compositions")).Should().Be(composition);
    }

    [Test]
    public async Task RepertoireMoveInvalidatesBothArtistScopesAndCompositions()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var song = Guid.NewGuid();
        var revision = await Revision(song, "repertoire_songs");
        await Sql(
            "INSERT INTO normalization.repertoire_songs(uuid,artist_uuid,display_title,revision_uuid) VALUES(@song,@first,'Song',@revision)",
            new
            {
                song,
                first,
                revision,
            }
        );
        var oldScope = await Epoch("repertoire", first);
        var newScope = await Epoch("repertoire", second);
        var compositions = await Epoch("compositions");

        await Sql(
            "UPDATE normalization.repertoire_songs SET artist_uuid=@second WHERE uuid=@song",
            new { second, song }
        );

        (await Epoch("repertoire", first)).Should().BeGreaterThan(oldScope);
        (await Epoch("repertoire", second)).Should().BeGreaterThan(newScope);
        (await Epoch("compositions")).Should().BeGreaterThan(compositions);
    }

    [Test]
    public async Task CurrentWorkLinkChangesInvalidateTheRepertoireAndCompositionUniverses()
    {
        var artist = Guid.NewGuid();
        var song = Guid.NewGuid();
        var composition = Guid.NewGuid();
        var songRevision = await Revision(song, "repertoire_songs");
        var workRevision = await Revision(composition, "compositions");
        await Sql(
            """
            INSERT INTO normalization.repertoire_songs(uuid,artist_uuid,display_title,revision_uuid) VALUES(@song,@artist,'Song',@songRevision);
            INSERT INTO normalization.compositions(uuid,display_title,revision_uuid) VALUES(@composition,'Song',@workRevision);
            """,
            new
            {
                song,
                artist,
                songRevision,
                composition,
                workRevision,
            }
        );
        var repertoire = await Epoch("repertoire", artist);
        var compositions = await Epoch("compositions");
        var link = Guid.NewGuid();
        var revision = await Revision(link, "repertoire_compositions");
        await Sql(
            "INSERT INTO normalization.repertoire_compositions(uuid,repertoire_song_uuid,composition_uuid,revision_uuid) VALUES(@link,@song,@composition,@revision)",
            new
            {
                link,
                song,
                composition,
                revision,
            }
        );
        (await Epoch("repertoire", artist)).Should().BeGreaterThan(repertoire);
        (await Epoch("compositions")).Should().BeGreaterThan(compositions);
    }

    [Test]
    public async Task AppendingUnreferencedMusicHistoryDoesNotChangeCandidateFacts()
    {
        var input = await fixture.Capture("Evidence", "songs");
        var beforePeople = await Epoch("people");
        var beforeVenues = await Epoch("venues");
        var beforeCompositions = await Epoch("compositions");
        var artist = Guid.NewGuid();
        var beforeRepertoire = await Epoch("repertoire", artist);
        var revision = await Revision(Guid.NewGuid(), "repertoire_songs");
        await Sql(
            """
            INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer) VALUES(@revision,0,@input,'/text');
            INSERT INTO normalization.music_exclusions(claim_hash,decision_uuid,payload) VALUES(@hash,@decision,'{}');
            INSERT INTO normalization.music_exclusion_evidence(claim_hash,decision_uuid,input_revision_uuid) VALUES(@hash,@decision,@input);
            """,
            new
            {
                revision,
                input = input.Reference.InputRevisionUuid,
                hash = Guid.NewGuid().ToString(),
                decision = Guid.NewGuid(),
            }
        );
        (await Epoch("people")).Should().Be(beforePeople);
        (await Epoch("venues")).Should().Be(beforeVenues);
        (await Epoch("compositions")).Should().Be(beforeCompositions);
        (await Epoch("repertoire", artist)).Should().Be(beforeRepertoire);
    }

    [Test]
    public async Task AppendingRecordingCreditEvidenceInvalidatesPeopleContext()
    {
        var input = await fixture.Capture("Recorder wording", "people");
        var credit = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO normalization.recording_credits(uuid,source_uuid,role,original_text,state,basis,reason_code,decision_uuid)
            VALUES(@credit,@source,'taper','Recorder wording','accepted','provider_assertion','explicit_credit',@decision)
            """,
            new
            {
                credit,
                source = Guid.NewGuid(),
                decision = Guid.NewGuid(),
            }
        );
        var before = await Epoch("people");
        await Sql(
            """
            INSERT INTO normalization.recording_credit_evidence(credit_uuid,input_revision_uuid,json_pointer,credit_revision)
            VALUES(@credit,@input,'/text',1)
            """,
            new { credit, input = input.Reference.InputRevisionUuid }
        );
        (await Epoch("people")).Should().BeGreaterThan(before);
    }

    [Test]
    public async Task AppendingVenueAssignmentEvidenceInvalidatesVenueFacts()
    {
        var input = await fixture.Capture("Venue wording", "venues");
        var venue = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind) VALUES(@venue,'Hall','hall','site');
            INSERT INTO normalization.imported_venue_assignments(uuid,imported_venue_uuid,canonical_venue_uuid,state,basis,reason_code,decision_uuid)
            VALUES(@assignment,@imported,@venue,'accepted','provider_assertion','explicit_venue',@decision)
            """,
            new
            {
                venue,
                assignment,
                imported = Guid.NewGuid(),
                decision = Guid.NewGuid(),
            }
        );
        var before = await Epoch("venues");
        await Sql(
            """
            INSERT INTO normalization.venue_assignment_evidence(imported_assignment_uuid,input_revision_uuid,json_pointer,assignment_revision)
            VALUES(@assignment,@input,'/text',1)
            """,
            new { assignment, input = input.Reference.InputRevisionUuid }
        );
        (await Epoch("venues")).Should().BeGreaterThan(before);
    }

    [TestCase("DELETE FROM normalization.candidate_universe_revisions WHERE domain='people'")]
    [TestCase("TRUNCATE normalization.candidate_universe_revisions CASCADE")]
    public async Task CandidateClockCannotBeRemoved(string sql)
    {
        var before = await Epoch("people");
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var remove = () => connection.ExecuteAsync(sql);
        await remove
            .Should()
            .ThrowAsync<PostgresException>()
            .WithMessage("*Retain candidate universe revisions*");
        (await Epoch("people")).Should().Be(before);
    }

    [TestCase("sources")]
    [TestCase("artists")]
    [TestCase("normalization.recording_credit_evidence")]
    public async Task OfflineTruncateAdvancesRetainedCandidateScopes(string relation)
    {
        var artist = Guid.NewGuid();
        var song = Guid.NewGuid();
        var revision = await Revision(song, "repertoire_songs");
        await Sql(
            "INSERT INTO normalization.repertoire_songs(uuid,artist_uuid,display_title,revision_uuid) VALUES(@song,@artist,'Song',@revision)",
            new
            {
                song,
                artist,
                revision,
            }
        );
        var before = await Epoch("people");
        var repertoire = await Epoch("repertoire", artist);
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync($"TRUNCATE {relation} CASCADE", transaction: transaction);
        (await Epoch(connection, transaction, "people")).Should().BeGreaterThan(before);
        (await Epoch(connection, transaction, "repertoire", artist))
            .Should()
            .BeGreaterThan(repertoire);
        await transaction.RollbackAsync();
        (await Epoch("people")).Should().Be(before);
        (await Epoch("repertoire", artist)).Should().Be(repertoire);
    }

    [Test]
    public async Task ArtistDeleteWaitsForMappingRegistryBeforeTakingThePeopleUniverse()
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider = await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name) SELECT GREATEST(COALESCE(max(id),0)+1,1000),@name FROM upstream_sources RETURNING id",
                new { name = "Candidate cascade " + Guid.NewGuid() }
            );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,'artist')",
                new { source.ArtistId, provider }
            );
        });
        await using var reader = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await reader.OpenAsync();
        await using var readerTransaction = await reader.BeginTransactionAsync();
        await reader.ExecuteAsync(
            "SELECT generation FROM normalization.catalog_mapping_registry_revision FOR SHARE",
            transaction: readerTransaction
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
                    "artist deletion must wait on the registry before its source/mapping cascades"
                );
            await reader.ExecuteAsync(
                "SET LOCAL lock_timeout='250ms'",
                transaction: readerTransaction
            );
            // A reader that holds the earlier registry clock must be able to take
            // the later candidate clock while deletion waits; the reverse order deadlocks.
            await reader.ExecuteAsync(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000' FOR UPDATE",
                transaction: readerTransaction
            );
        }
        finally
        {
            await readerTransaction.RollbackAsync();
            await deletion;
        }
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM artists WHERE id=@ArtistId)",
                    source
                )
            )
        )
            .Should()
            .BeFalse();
    }

    private async Task<Guid> Revision(Guid entity, string kind)
    {
        var revision = Guid.NewGuid();
        await Sql(
            """
            INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence)
            VALUES(@revision,@entity,@kind,1,@decision,'provider_assertion','{}','[]')
            """,
            new
            {
                revision,
                entity,
                kind,
                decision = Guid.NewGuid(),
            }
        );
        return revision;
    }

    private Task<int> Sql(string sql, object? parameters = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, parameters));

    private Task<long> Epoch(string domain, Guid scope = default) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT COALESCE((SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@domain AND scope_uuid=@scope),0)",
                new { domain, scope }
            )
        );

    private static Task<long> Epoch(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string domain,
        Guid scope = default
    ) =>
        connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE((SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@domain AND scope_uuid=@scope),0)",
            new { domain, scope },
            transaction
        );
}
