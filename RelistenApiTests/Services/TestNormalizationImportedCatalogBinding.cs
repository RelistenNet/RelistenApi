using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Retained catalog facts bind only with the original ownership and producer revisions.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationImportedCatalogBinding
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationImportedCatalogService imported = null!;
    private NormalizationCatalogBindingService bindings = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        imported = new(fixture.Db, evidence);
        bindings = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task VenueBindingKeepsStoredIdentityAndExactReadSet(bool owned)
    {
        var artist = await Artist();
        var venue = await Venue(owned ? artist.Id : null);
        var snapshot = (await imported.RefreshImportedVenueAsync(venue))!;
        var bound = await bindings.BindRefreshedImportedVenueAsync(snapshot);

        bound.Venue.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        bound
            .Venue.Value.Should()
            .Be(new NormalizationCatalogImportedVenueReference(venue, owned ? artist.Uuid : null));
        bound.CatalogInputs.Should().Equal(snapshot.CatalogInputs);
        bound.ImportedVenues.Should().Equal(snapshot.ImportedVenues);
        bound
            .Inputs.Should()
            .Equal(
                new NormalizationBindingInputReference(
                    snapshot.Capture.Revision.Uuid,
                    snapshot.Capture.Revision.Resource
                )
            );
    }

    [Test]
    public async Task DictionaryBindingPreservesEmptyNamesAndSeparatesIdenticalTitlesAcrossArtists()
    {
        var firstArtist = await Artist();
        var secondArtist = await Artist();
        var first = await Song(firstArtist.Id, "Shared title");
        var empty = await Song(firstArtist.Id, "");
        var second = await Song(secondArtist.Id, "Shared title");
        var firstSnapshot = (await imported.RefreshArtistRepertoireAsync(firstArtist.Uuid))!;
        var secondSnapshot = (await imported.RefreshArtistRepertoireAsync(secondArtist.Uuid))!;

        var firstBound = await bindings.BindRefreshedRepertoireAsync(firstSnapshot);
        var secondBound = await bindings.BindRefreshedRepertoireAsync(secondSnapshot);

        firstBound.Artist.Value!.ArtistUuid.Should().Be(firstArtist.Uuid);
        firstBound
            .Songs.Select(s => s.Binding.Value!.ImportedSongUuid)
            .Should()
            .BeEquivalentTo([first, empty]);
        firstBound.Songs.Should().OnlyContain(s => s.Binding.Value!.ArtistUuid == firstArtist.Uuid);
        firstBound
            .Songs.Single(s => s.Binding.Value!.ImportedSongUuid == empty)
            .Observation.Title!.Value.Should()
            .Be("");
        secondBound
            .Songs.Single()
            .Binding.Value.Should()
            .Be(new NormalizationCatalogImportedSongReference(secondArtist.Uuid, second));
        firstBound.CatalogInputs.Should().Equal(firstSnapshot.CatalogInputs);
        firstBound.Inputs.Single().Resource.Should().Be(firstSnapshot.Capture.Revision.Resource);
        firstBound
            .Songs.Should()
            .OnlyContain(s =>
                s.Observation.Origin.Evidence.InputRevisionUuid
                == firstSnapshot.Capture.Revision.Uuid
            );
    }

    [Test]
    public async Task KnownEmptyArtistHasABoundEmptyDictionary()
    {
        var artist = await Artist();
        var snapshot = (await imported.RefreshArtistRepertoireAsync(artist.Uuid))!;
        var bound = await bindings.BindRefreshedRepertoireAsync(snapshot);
        bound.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        bound.Songs.Should().BeEmpty();
        bound.CatalogInputs.Should().ContainSingle().Which.ArtistUuid.Should().Be(artist.Uuid);
    }

    [TestCase("name='Corrected'")]
    [TestCase("uuid=gen_random_uuid()")]
    public async Task VenueChangesRequireAWholeRefresh(string change)
    {
        var venue = await Venue();
        var snapshot = (await imported.RefreshImportedVenueAsync(venue))!;
        await Execute($"UPDATE venues SET {change} WHERE uuid=@venue", new { venue });
        var bind = () => bindings.BindRefreshedImportedVenueAsync(snapshot);
        await bind.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task FreshWrapperClocksCannotMakeOlderVenueWordingCurrent()
    {
        var venue = await Venue();
        var old = (await imported.RefreshImportedVenueAsync(venue))!;
        await Execute("UPDATE venues SET name='Corrected' WHERE uuid=@venue", new { venue });
        var fresh = (await imported.RefreshImportedVenueAsync(venue))!;
        var relabeled = old with
        {
            CatalogInputs = fresh.CatalogInputs,
            ImportedVenues = fresh.ImportedVenues,
        };
        var bind = () => bindings.BindRefreshedImportedVenueAsync(relabeled);
        await bind.Should().ThrowAsync<NormalizationConflictException>();
        (await bindings.BindRefreshedImportedVenueAsync(fresh))
            .Venue.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
    }

    [Test]
    public async Task RetainedFabricatedVenueFactsDoNotEstablishCatalogAuthority()
    {
        var venue = await Venue();
        var original = (await imported.RefreshImportedVenueAsync(venue))!;
        var payload = JsonNode.Parse(original.Capture.Revision.Payload)!;
        payload["venue"]!["name"] = "Invented wording";
        var forged = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                original.Capture.Revision.Resource,
                System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString()),
                original.Capture.Revision.MediaType,
                original.Capture.Revision.CaptureVersion,
                original.Capture.Fetch.ResourceGeneration,
                DateTimeOffset.UtcNow
            )
        );

        var bind = () =>
            bindings.BindRefreshedImportedVenueAsync(original with { Capture = forged });
        await bind.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ForgedReceiptCannotBindOtherwiseAccurateFacts()
    {
        var venue = await Venue();
        var original = (await imported.RefreshImportedVenueAsync(venue))!;
        var actual = original.Capture.Fetch;
        var receipt = new NormalizationInputFetch
        {
            FetchUuid = Guid.NewGuid(),
            InputRevisionUuid = actual.InputRevisionUuid,
            ExpectedGeneration = actual.ExpectedGeneration,
            ResourceGeneration = actual.ResourceGeneration,
            AcceptedAsCurrent = true,
        };
        var bind = () =>
            bindings.BindRefreshedImportedVenueAsync(
                original with
                {
                    Capture = new(original.Capture.Revision, receipt),
                }
            );
        await bind.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task MovedSongRequiresRefreshingBothArtistDictionaries()
    {
        var oldArtist = await Artist();
        var newArtist = await Artist();
        var song = await Song(oldArtist.Id, "Song");
        var oldSnapshot = (await imported.RefreshArtistRepertoireAsync(oldArtist.Uuid))!;
        var newSnapshot = (await imported.RefreshArtistRepertoireAsync(newArtist.Uuid))!;
        await Execute(
            "UPDATE setlist_songs SET artist_id=@Id WHERE uuid=@song",
            new { newArtist.Id, song }
        );

        var oldBind = () => bindings.BindRefreshedRepertoireAsync(oldSnapshot);
        var newBind = () => bindings.BindRefreshedRepertoireAsync(newSnapshot);
        await oldBind.Should().ThrowAsync<NormalizationConflictException>();
        await newBind.Should().ThrowAsync<NormalizationConflictException>();
        var current = (await imported.RefreshArtistRepertoireAsync(newArtist.Uuid))!;
        (await bindings.BindRefreshedRepertoireAsync(current))
            .Songs.Single()
            .Binding.Value!.ImportedSongUuid.Should()
            .Be(song);
    }

    [Test]
    public async Task NewArtistClockCannotBeAttachedToAnOlderDictionary()
    {
        var artist = await Artist();
        var song = await Song(artist.Id, "Old title");
        var old = (await imported.RefreshArtistRepertoireAsync(artist.Uuid))!;
        await Execute(
            "UPDATE setlist_songs SET name='Current title' WHERE uuid=@song",
            new { song }
        );
        var fresh = (await imported.RefreshArtistRepertoireAsync(artist.Uuid))!;
        var bind = () =>
            bindings.BindRefreshedRepertoireAsync(old with { CatalogInputs = fresh.CatalogInputs });
        await bind.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task RestoredVenueWordingDoesNotRestoreAnOlderProducerRevision()
    {
        var venue = await Venue();
        var old = (await imported.RefreshImportedVenueAsync(venue))!;
        await Execute(
            "UPDATE venues SET name='Temporary correction' WHERE uuid=@venue",
            new { venue }
        );
        await Execute("UPDATE venues SET name='Hall' WHERE uuid=@venue", new { venue });
        var fresh = (await imported.RefreshImportedVenueAsync(venue))!;

        var bind = () =>
            bindings.BindRefreshedImportedVenueAsync(
                old with
                {
                    ImportedVenues = fresh.ImportedVenues,
                }
            );
        await bind.Should().ThrowAsync<NormalizationConflictException>();
        (await bindings.BindRefreshedImportedVenueAsync(fresh))
            .Venue.Value!.VenueUuid.Should()
            .Be(venue);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CheckedDictionaryCanBePublishedWithItsCompleteReadSet(bool includeSong)
    {
        var artist = await Artist();
        if (includeSong)
        {
            await Song(artist.Id, "Song");
        }
        var snapshot = (await imported.RefreshArtistRepertoireAsync(artist.Uuid))!;
        var bound = await bindings.BindRefreshedRepertoireAsync(snapshot);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "artist_repertoire", artist.Uuid),
            "test-policy",
            [bound.Resource],
            [],
            catalogInputs: bound.CatalogInputs,
            expectedInputRevisions: bound.Inputs
        );
        var callbackCalled = false;
        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                callbackCalled = true;
                return Task.CompletedTask;
            }
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        callbackCalled.Should().BeTrue();
    }

    [Test]
    public async Task CallerMutationAfterBindingStartsCannotReplaceVerifiedBytesOrClocks()
    {
        var artist = await Artist();
        var venue = await Venue(artist.Id);
        var snapshot = (await imported.RefreshImportedVenueAsync(venue))!;
        var artists = snapshot.CatalogInputs.ToArray();
        var venues = snapshot.ImportedVenues.ToArray();
        var originalArtist = artists.Single();
        var originalVenue = venues.Single();
        snapshot = snapshot with { CatalogInputs = artists, ImportedVenues = venues };
        await using var connection = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "LOCK TABLE normalization.input_fetches IN ACCESS EXCLUSIVE MODE",
            transaction: transaction
        );

        var pending = bindings.BindRefreshedImportedVenueAsync(snapshot);
        // Preparation is synchronous before the first database await. The lock keeps receipt
        // validation pending while the caller replaces all mutable parts of its input.
        Array.Fill(snapshot.Capture.Revision.Payload, (byte)0);
        artists[0] = new(Guid.NewGuid(), long.MaxValue);
        venues[0] = new(Guid.NewGuid(), long.MaxValue);
        await transaction.CommitAsync();

        var bound = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        bound.Venue.Value!.VenueUuid.Should().Be(venue);
        bound.CatalogInputs.Should().Equal(originalArtist);
        bound.ImportedVenues.Should().Equal(originalVenue);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CheckedVenueReadSetPublishesOnlyWhileItsFactsRemainCurrent(bool changeVenue)
    {
        var venue = await Venue();
        var snapshot = (await imported.RefreshImportedVenueAsync(venue))!;
        var bound = await bindings.BindRefreshedImportedVenueAsync(snapshot);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("venues", "venue", Guid.NewGuid()),
            "test-policy",
            [bound.Resource],
            [],
            catalogInputs: bound.CatalogInputs,
            expectedInputRevisions: bound.Inputs,
            importedVenues: bound.ImportedVenues
        );
        if (changeVenue)
        {
            await Execute(
                "UPDATE venues SET location='Corrected city' WHERE uuid=@venue",
                new { venue }
            );
        }

        var callbackCalled = false;
        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                callbackCalled = true;
                return Task.CompletedTask;
            }
        );
        publication
            .Status.Should()
            .Be(
                changeVenue
                    ? NormalizationPublicationStatus.Stale
                    : NormalizationPublicationStatus.Published
            );
        callbackCalled.Should().Be(!changeVenue);
    }

    private async Task<(int Id, Guid Uuid)> Artist()
    {
        var source = await fixture.Source();
        var uuid = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        return (source.ArtistId, uuid);
    }

    private async Task<Guid> Venue(int? artist = null)
    {
        var uuid = Guid.NewGuid();
        await Execute(
            "INSERT INTO venues(uuid,artist_id,name,slug,upstream_identifier,location,updated_at) VALUES(@uuid,@artist,'Hall',@key,@key,'City',now())",
            new
            {
                uuid,
                artist,
                key = uuid.ToString("D"),
            }
        );
        return uuid;
    }

    private async Task<Guid> Song(int artist, string name)
    {
        var uuid = Guid.NewGuid();
        await Execute(
            "INSERT INTO setlist_songs(artist_id,uuid,name,slug,upstream_identifier,updated_at) VALUES(@artist,@uuid,@name,@key,@key,now())",
            new
            {
                artist,
                uuid,
                name,
                key = uuid.ToString("D"),
            }
        );
        return uuid;
    }

    private Task Execute(string sql, object args) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, args));
}
