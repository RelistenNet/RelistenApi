using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Services;

/// <summary>
/// Retained setlists, catalog engagements and accepted structure have separate publication
/// boundaries. A source-free engagement changes catalog evidence without moving playback.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistEngagementBoundary
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationSetlistProcessor processor = null!;
    private NormalizedMusicService music = null!;
    private NormalizationReadService normalized = null!;
    private ShowPerformanceService performances = null!;
    private CatalogRebuildService rebuild = null!;
    private ShowService legacy = null!;
    private RedisService redis = null!;
    private EmptyShowPopularity popularity = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        evidence = new(fixture.Db);
        processor = new(new(fixture.Db), evidence, new(fixture.Db), new(), new(), fixture.Work);
        music = new(fixture.Db, fixture.Work);
        normalized = new(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        performances = new(fixture.Db);
        redis = new(RedisService.BuildConfiguration("redis://127.0.0.1:16379"));
        rebuild = new(fixture.Db, redis);
        popularity = new(fixture.Db, redis);
        legacy = new(
            fixture.Db,
            new(fixture.Db, new(fixture.Db)),
            new Lazy<PopularityService>(() => popularity.Service)
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        redis?.connection.Dispose();
        fixture?.Dispose();
    }

    [Test]
    public async Task NewLateEngagementRequiresFreshProposalAndExposesSetlistWithoutInventingPlayback()
    {
        var context = await Context("17-10-2026");
        var defaultShow = (await legacy.PerformancesForArtistOnDate(context.Artist, "2026-10-17"))
            .Should()
            .ContainSingle()
            .Which;
        var originalPlayback = PlaybackImage([defaultShow]);
        var importedSetlistId = await ImportedSetlist(context);
        var songUuid = await PublishSong(context);
        var original = await Propose(context);
        var planned = Structure(context, original, null, songUuid);
        var oldWork = await Admit(original, planned);

        var late = await performances.CreateOrSelectPerformanceAsync(
            context.Artist,
            new PerformanceEngagementIdentity.Reviewed(
                "review:late-" + Guid.NewGuid().ToString("N")
            ),
            new PerformanceEngagementTarget.CreateNew(),
            "2026-10-17",
            new(PerformanceType.Late, "Late concert")
        );
        late.uuid.Should().NotBe(defaultShow.uuid);
        var assigned = planned with { Edition = planned.Edition with { ShowUuid = late.uuid } };

        var stale = await music.PublishSetlistStructureAsync(oldWork.WorkUuid, assigned);

        stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
        (await music.GetSetlistStructureAsync(planned.Edition.Uuid)).Should().BeNull();
        (await normalized.ListShowSetlistsAsync(late.uuid))!.items.Should().BeEmpty();
        (await fixture.Work.GetSubjectAsync(oldWork.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .BeNull();

        await performances.AssignSetlistAsync(
            context.Artist,
            importedSetlistId,
            late.uuid,
            JsonSerializer.Serialize(
                new
                {
                    input_revision_uuid = context.Input.Uuid,
                    account = context.AccountIdentity,
                    reason = "Reviewed distinct late engagement",
                }
            )
        );
        await rebuild.RebuildShows(context.Artist);
        await rebuild.RebuildYears(context.Artist);
        var refreshed = await Propose(context);
        refreshed
            .Preparation.Inputs.Single()
            .Accounts.Single()
            .Binding.Performance.Value!.ShowUuid.Should()
            .Be(late.uuid);
        refreshed.Ticket.CatalogInputs.Should().ContainSingle();
        refreshed
            .Ticket.CatalogInputs!.Single()
            .Generation.Should()
            .NotBe(original.Ticket.CatalogInputs!.Single().Generation);
        var current = Structure(context, refreshed, late.uuid, songUuid, planned);
        var work = await Admit(refreshed, current);
        (await music.PublishSetlistStructureAsync(work.WorkUuid, current))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var accepted = (await normalized.ListShowSetlistsAsync(late.uuid))!
            .items.Should()
            .ContainSingle()
            .Which;
        accepted.uuid.Should().Be(current.Edition.Uuid);
        accepted.show_uuid.Should().Be(late.uuid);
        (await normalized.ListArtistSetlistsAsync(context.Artist.uuid.ToString("D")))
            .items.Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(accepted.uuid);
        var occurrence = (await normalized.ListOccurrencesAsync(accepted.uuid))!
            .items.Should()
            .ContainSingle()
            .Which;
        occurrence.repertoire_song_uuid.Should().Be(songUuid);
        occurrence.edition.show_uuid.Should().Be(late.uuid);
        // This is the actual public playback query. A setlist does not make audio available.
        (await normalized.ListSongShowsAsync(songUuid))!
            .items.Should()
            .BeEmpty();
        (await legacy.ShowWithSourcesForUuid(context.Artist, late.uuid)).Should().BeNull();
        (await normalized.ListShowSetlistsAsync(defaultShow.uuid))!.items.Should().BeEmpty();
        PlaybackImage(await legacy.PerformancesForArtistOnDate(context.Artist, "2026-10-17"))
            .Should()
            .Be(originalPlayback);
        (await music.GetSetlistStructureAsync(current.Edition.Uuid))!
            .Value.Sources.Single()
            .ProposalAttemptUuid.Should()
            .Be(refreshed.AttemptUuid);
    }

    [Test]
    public async Task UnknownYearSetlistAcceptsLiteralUnassignedStructureWithoutCreatingAShow()
    {
        const string unknownDate = "XXXX-XX-XX";
        var context = await Context(unknownDate);
        var before = PlaybackImage(
            await legacy.PerformancesForArtistOnDate(context.Artist, "2026-10-17")
        );
        var showCount = await ShowCount(context.Artist);
        var proposal = await Propose(context);
        var binding = proposal.Preparation.Inputs.Single().Accounts.Single().Binding;
        binding.Performance.Value.Should().BeNull();
        var structure = Structure(context, proposal, null, null);
        var ticket = await Admit(proposal, structure);

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var accepted = (await normalized.ListArtistSetlistsAsync(context.Artist.uuid.ToString("D")))
            .items.Should()
            .ContainSingle()
            .Which;
        accepted.literal_date.Should().Be(unknownDate);
        accepted.show_uuid.Should().BeNull();
        var occurrence = (await normalized.ListOccurrencesAsync(accepted.uuid))!
            .items.Should()
            .ContainSingle()
            .Which;
        occurrence.edition.literal_date.Should().Be(unknownDate);
        occurrence.edition.show_uuid.Should().BeNull();
        (await music.GetSetlistStructureAsync(accepted.uuid))!
            .Value.Sources.Single()
            .Account.Account.LiteralDate!.Value.Should()
            .Be(unknownDate);
        (await ShowCount(context.Artist)).Should().Be(showCount);
        PlaybackImage(await legacy.PerformancesForArtistOnDate(context.Artist, "2026-10-17"))
            .Should()
            .Be(before);
    }

    private async Task<ContextState> Context(string eventDate)
    {
        var source = await fixture.Source();
        var artistKey = Guid.NewGuid().ToString("D");
        var artist = await fixture.Db.WithWriteConnection(async c =>
        {
            var provider = await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name='setlist.fm' ORDER BY id LIMIT 1"
            );
            provider ??= await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'setlist.fm','https://setlist.fm' FROM upstream_sources RETURNING id"
            );
            await c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId; INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@artistKey)",
                new
                {
                    source.ArtistId,
                    artistKey,
                    provider,
                }
            );
            return await c.QuerySingleAsync<Artist>(
                "SELECT * FROM artists WHERE id=@ArtistId",
                source
            );
        });
        artist.features = new Features();
        await popularity.PrepareArtistAsync(artist.uuid);
        await rebuild.RebuildShows(artist);
        await rebuild.RebuildYears(artist);
        var accountKey = "engagement-" + Guid.NewGuid().ToString("N");
        var resource = new NormalizationResourceKey(
            "setlist.fm",
            "setlist-page",
            artistKey + ":page=1"
        );
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        page = 1,
                        itemsPerPage = 20,
                        total = 1,
                        setlist = new[]
                        {
                            new
                            {
                                id = accountKey,
                                eventDate,
                                artist = new { mbid = artistKey, name = "Boundary artist" },
                                sets = new
                                {
                                    set = new[]
                                    {
                                        new
                                        {
                                            name = "Late set",
                                            song = new[] { new { name = "Boundary Song" } },
                                        },
                                    },
                                },
                            },
                        },
                    }
                ),
                "application/json",
                "setlist-fm-http-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var account = new NormalizationProviderDecoder()
            .Decode(captured.Revision)
            .Setlists.Single();
        return new(artist, captured.Revision, account.Origin.Item);
    }

    private Task<long> ImportedSetlist(ContextState context) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var venue = await c.QuerySingleAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@id,'Boundary Hall','Boundary Town',@uuid,now()) RETURNING id",
                new { context.Artist.id, uuid = Guid.NewGuid() }
            );
            return await c.QuerySingleAsync<long>(
                "INSERT INTO setlist_shows(artist_id,venue_id,date,upstream_identifier,uuid,updated_at) VALUES(@id,@venue,'2026-10-17',@key,@uuid,now()) RETURNING id",
                new
                {
                    context.Artist.id,
                    venue,
                    key = context.AccountIdentity.ProviderKey,
                    uuid = Guid.NewGuid(),
                }
            );
        });

    private async Task<NormalizationSetlistProposalBatch> Propose(ContextState context)
    {
        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(context.Input.Uuid, context.Input.Resource),
            context.AccountIdentity
        );
        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return (await processor.GetAsync(attempt.Registration.RegistrationUuid))!;
    }

    private async Task<Guid> PublishSong(ContextState context)
    {
        var title = new NormalizationProviderDecoder()
            .Decode(context.Input)
            .Setlists.Single()
            .Passages.Single()
            .Title;
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "artist_repertoire", context.Artist.uuid),
            "boundary-song-v1",
            [context.Input.Resource],
            [],
            expectedInputRevisions: [new(context.Input.Uuid, context.Input.Resource)]
        );
        var song = new RepertoireSongChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = context.Artist.uuid,
            DisplayTitle = title.Value,
            Evidence = [title.Evidence],
        };
        (await music.PublishAsync(ticket.WorkUuid, new() { RepertoireSongs = [song] }))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return song.Uuid;
    }

    private static SetlistStructureChange Structure(
        ContextState context,
        NormalizationSetlistProposalBatch batch,
        Guid? showUuid,
        Guid? songUuid,
        SetlistStructureChange? previous = null
    )
    {
        var proposal = batch.Extractions.Single().Accounts.Single();
        var account = proposal.Account;
        var observedSet = account.Sets.Single();
        var passage = proposal.Passages.Single();
        var editionUuid = previous?.Edition.Uuid ?? Guid.NewGuid();
        var setUuid = previous?.Sets.Single().Uuid ?? Guid.NewGuid();
        var occurrenceUuid = previous?.Occurrences.Single().Uuid ?? Guid.NewGuid();
        return new()
        {
            Uuid = previous?.Uuid ?? Guid.NewGuid(),
            Evidence = [account.Origin.Evidence],
            Edition = new()
            {
                Uuid = editionUuid,
                ArtistUuid = context.Artist.uuid,
                ShowUuid = showUuid,
                LiteralDate = account.LiteralDate!.Value,
                Ordered = account.Ordered,
                Provider = account.Origin.Item.Provider,
                ResourceType = account.Origin.Item.ResourceType,
                ProviderKey = account.Origin.Item.ProviderKey,
                Evidence = [account.Origin.Evidence],
            },
            Sets =
            [
                new()
                {
                    Uuid = setUuid,
                    Position = observedSet.Ordinal,
                    Label = observedSet.Label?.Value,
                    Evidence = [observedSet.Evidence],
                },
            ],
            Occurrences =
            [
                new()
                {
                    Uuid = occurrenceUuid,
                    EditionUuid = editionUuid,
                    SetUuid = setUuid,
                    Position = passage.Passage.Ordinal,
                    SetLabel = passage.Passage.SetLabel?.Value,
                    Kind = "song",
                    RepertoireSongUuid = songUuid,
                    OriginalText = passage.Passage.Title.Value,
                    Evidence = [passage.Passage.Title.Evidence],
                },
            ],
            Sources = [new(batch.AttemptUuid, context.Input.Uuid, 0, proposal)],
            SetCorrespondence = [new(0, 0, setUuid)],
            PassageCorrespondence = [new(0, passage.ProposalUuid, [occurrenceUuid])],
        };
    }

    private async Task<NormalizationWorkTicket> Admit(
        NormalizationSetlistProposalBatch proposal,
        SetlistStructureChange structure
    )
    {
        var dependencies = new List<Guid> { proposal.Ticket.SubjectUuid };
        if (structure.Occurrences.Any(occurrence => occurrence.RepertoireSongUuid.HasValue))
        {
            dependencies.Add(
                await fixture.OwnerUuid("songs", "artist_repertoire", structure.Edition.ArtistUuid)
            );
        }
        var owner = new NormalizationSubjectKey("songs", "setlist", structure.Edition.Uuid);
        var state = await fixture.Work.GetSubjectAsync(owner);
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            owner,
            "engagement-boundary-v1",
            proposal.Preparation.Inputs.Select(input => input.Input.Resource).ToArray(),
            dependencies,
            state?.DesiredGeneration,
            catalogInputs: proposal.Ticket.CatalogInputs,
            mappingRegistry: proposal.Ticket.MappingRegistry,
            expectedInputRevisions: proposal
                .Preparation.Inputs.Select(input => input.Input)
                .ToArray()
        );
    }

    private Task<long> ShowCount(Artist artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM shows WHERE artist_id=@id",
                new { artist.id }
            )
        );

    private static string PlaybackImage(IReadOnlyList<ShowWithSources> shows) =>
        JsonSerializer.Serialize(
            shows.Select(show => new
            {
                show.uuid,
                show.display_date,
                sources = show.sources.Select(source => new
                {
                    source.uuid,
                    source.show_uuid,
                    sets = source.sets.Select(set => new
                    {
                        set.uuid,
                        tracks = set.tracks.Select(track => new { track.uuid, track.mp3_url }),
                    }),
                }),
            })
        );

    private sealed record ContextState(
        Artist Artist,
        NormalizationInputRevision Input,
        NormalizationResourceKey AccountIdentity
    );
}
