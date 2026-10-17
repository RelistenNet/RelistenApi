using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Jerry mirror accounts bind through their configured catalog and stored setlist assignment.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationJerryBinding
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationCatalogBindingService bindings = null!;
    private IHost host = null!;

    private const string ShowIdentifier = "1972-08-27-synthetic-venue";
    private const string SyntheticMirrorHtml = """
        <p class="venue-name">Synthetic Jerry Hall</p><p class="venue-name">Jerry Garcia Band</p>
        <p class="venue-name">Synthetic Tour</p><p class="venue-address">Veneta, OR</p>
        <p class="venue-country">USA</p><time datetime="1972-08-27"></time>
        <section data-set="Set 1"><ol>
        <li><a href="/song/dark-star/">Dark Star</a> &gt;</li>
        <li><a href="/song/drums/">Drums</a>*</li>
        <li><a href="/song/dark-star/">Dark Star</a></li>
        </ol></section>
        """;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        bindings = new(fixture.Db);
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Db);
        builder.Services.AddSingleton(_ => new RedisService(
            RedisService.BuildConfiguration("redis://127.0.0.1:16379")
        ));
        builder.Services.AddRelistenCatalogServices();
        builder.Services.Configure<NormalizationProviderCaptureOptions>(options =>
            options.Enabled = true
        );
        host = builder.Build();
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (host != null)
        {
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }
        fixture?.Dispose();
    }

    [Test]
    public async Task SyntheticImporterPageBindsActualSetlistAndExplicitLatePerformance()
    {
        var artist = await NewArtist();
        var prefix = "synthetic/nested-" + Guid.NewGuid();
        await Map(artist, prefix);
        var context = new ProviderContext(
            artist,
            new ArtistUpstreamSource { upstream_identifier = prefix },
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1)),
            new CatalogImportTransaction(fixture.Db),
            CancellationToken.None
        );
        var item = new DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject>(
            new(ShowIdentifier),
            new()
            {
                Identifier = ShowIdentifier,
                FilePath = "moved-directory/" + ShowIdentifier + ".html",
                Date = new(1972, 8, 27),
                DisplayDate = "1972-08-27",
            }
        );
        using var scope = host.Services.CreateScope();
        using var importer = scope.ServiceProvider.GetRequiredService<JerryGarciaComImporter>();
        importer.UseIngestionHttpClient(new HttpClient(new SyntheticMirrorHandler()));
        (await importer.ImportPageAsync(context, item))
            .Disposition.Should()
            .Be(ItemDisposition.Applied);
        var input = (await evidence.GetCurrentInputAsync(Resource(prefix)))!.Revision;
        var setlist = (await new SetlistShowService(fixture.Db).AllForArtist(artist)).Single();
        var early = await Show(artist, "early");
        var late = await Show(artist, "late");
        await new ShowPerformanceService(fixture.Db).AssignSetlistAsync(artist, setlist.id, late);
        var account = Decode(input);

        var result = await bindings.BindSetlistAsync(input, account, artist.uuid);

        result.Artist.Value!.ArtistUuid.Should().Be(artist.uuid);
        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(setlist.uuid);
        result.Performance.Value!.ShowUuid.Should().Be(late).And.NotBe(early);
        result.Account.Should().BeEquivalentTo(account.Origin);
        account
            .Passages.Select(p => p.Title.Value)
            .Should()
            .Equal("Dark Star", "Drums", "Dark Star");
        account.Passages[0].SongIdentities.Single().Identity.ProviderKey.Should().Be("dark-star");
        account.Passages[2].OccurrenceIdentity.Should().BeNull();
        result.Inputs.Should().ContainSingle().Which.InputRevisionUuid.Should().Be(input.Uuid);
        result.CatalogInputs.Should().ContainSingle().Which.ArtistUuid.Should().Be(artist.uuid);
        result.MappingRegistry.Should().NotBeNull();
    }

    [Test]
    public async Task MissingMappingRetainsAccountAndItsAbsenceFence()
    {
        var artist = await NewArtist();
        var prefix = "missing-" + Guid.NewGuid();
        var input = await Capture(prefix);
        var result = await bindings.BindSetlistAsync(input, Decode(input));
        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Artist.Reason.Should().Be("missing_artist_mapping");
        result.Account.Should().BeEquivalentTo(Decode(input).Origin);
        result.MappingRegistry.Should().NotBeNull();
        var ticket = await Begin(result);
        await Map(artist, prefix);
        var called = false;

        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );

        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
    }

    [Test]
    public async Task MappedArtistRemainsAvailableBeforeItsImportedSetlistExists()
    {
        var artist = await NewArtist();
        var prefix = "missing-setlist-" + Guid.NewGuid();
        await Map(artist, prefix);
        var input = await Capture(prefix);
        var result = await bindings.BindSetlistAsync(input, Decode(input));
        result.Artist.Value!.ArtistUuid.Should().Be(artist.uuid);
        result.ImportedSetlist.Reason.Should().Be("missing_imported_setlist");
        result.Performance.Value.Should().BeNull();
        result.CatalogInputs.Should().ContainSingle().Which.ArtistUuid.Should().Be(artist.uuid);
        var ticket = await Begin(result);
        await ImportedSetlist(artist);

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task DuplicateMappingIsAmbiguousBeforeExpectedArtistFiltering()
    {
        var first = await NewArtist();
        var second = await NewArtist();
        var prefix = "ambiguous-" + Guid.NewGuid();
        await Map(first, prefix);
        await Map(second, prefix);
        await ImportedSetlist(first);
        var input = await Capture(prefix);

        var result = await bindings.BindSetlistAsync(input, Decode(input), first.uuid);

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        result.Artist.Reason.Should().Be("ambiguous_artist_mapping");
        result.ImportedSetlist.Value.Should().BeNull();
        result
            .CatalogInputs.Select(x => x.ArtistUuid)
            .Should()
            .BeEquivalentTo([first.uuid, second.uuid]);
    }

    [Test]
    public async Task ExpectedArtistCannotRetargetAnotherMappedCatalog()
    {
        var artist = await NewArtist();
        var prefix = "wrong-artist-" + Guid.NewGuid();
        await Map(artist, prefix);
        await ImportedSetlist(artist);
        var input = await Capture(prefix);

        var result = await bindings.BindSetlistAsync(input, Decode(input), Guid.NewGuid());

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Artist.Reason.Should().Be("unexpected_artist");
        result.Performance.Value.Should().BeNull();
    }

    [Test]
    public async Task AnotherProvidersMatchingPrefixDoesNotQualifyJerry()
    {
        var artist = await NewArtist();
        var prefix = "other-provider-" + Guid.NewGuid();
        await Map(artist, prefix, "archive.org");
        await ImportedSetlist(artist);
        var input = await Capture(prefix);

        var result = await bindings.BindSetlistAsync(input, Decode(input));

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Artist.Reason.Should().Be("missing_artist_mapping");
        result.ImportedSetlist.Value.Should().BeNull();
    }

    [TestCase(1, "1972-08-27")]
    [TestCase(2, "1972-08-27")]
    [TestCase(1, "1972-xx-xx")]
    public async Task MatchingDateDoesNotCreateAPerformanceAssignment(
        int performanceCount,
        string literalDate
    )
    {
        var artist = await NewArtist();
        var prefix = "unassigned-" + Guid.NewGuid();
        await Map(artist, prefix);
        var setlist = await ImportedSetlist(artist);
        await Show(artist, "early");
        if (performanceCount == 2)
        {
            await Show(artist, "late");
        }
        var input = await Capture(
            prefix,
            SyntheticMirrorHtml.Replace("datetime=\"1972-08-27\"", $"datetime=\"{literalDate}\"")
        );

        var result = await bindings.BindSetlistAsync(input, Decode(input));

        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(setlist.uuid);
        result.Performance.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Performance.Reason.Should().Be("missing_setlist_assignment");
        Decode(input).LiteralDate!.Value.Should().Be(literalDate);
    }

    [Test]
    public async Task ForeignPerformanceIsUnresolvedAndBothOwnersAreFenced()
    {
        var artist = await NewArtist();
        var other = await NewArtist();
        var prefix = "foreign-show-" + Guid.NewGuid();
        await Map(artist, prefix);
        var setlist = await ImportedSetlist(artist);
        var foreignShow = await Show(other, "late");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO setlist_show_assignments(setlist_show_id,show_id,evidence)
                SELECT @id,id,'{}'::jsonb FROM shows WHERE uuid=@foreignShow
                """,
                new { setlist.id, foreignShow }
            )
        );
        var input = await Capture(prefix);

        var result = await bindings.BindSetlistAsync(input, Decode(input));

        result.Artist.Value!.ArtistUuid.Should().Be(artist.uuid);
        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(setlist.uuid);
        result.Performance.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Performance.Reason.Should().Be("performance_artist_conflict");
        result
            .CatalogInputs.Select(x => x.ArtistUuid)
            .Should()
            .BeEquivalentTo([artist.uuid, other.uuid]);
    }

    [Test]
    public async Task ExistingForeignVenueOwnerJoinsTheAccountReadSet()
    {
        var artist = await NewArtist();
        var other = await NewArtist();
        var prefix = "foreign-venue-" + Guid.NewGuid();
        await Map(artist, prefix);
        var setlist = await ImportedSetlist(artist);
        var venueUuid = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "UPDATE venues SET artist_id=@other WHERE id=@venue RETURNING uuid",
                new { other = other.id, venue = setlist.venue_id }
            )
        );
        var input = await Capture(prefix);

        var result = await bindings.BindSetlistAsync(input, Decode(input));

        result.ImportedSetlist.Value!.VenueUuid.Should().Be(venueUuid);
        result
            .CatalogInputs.Select(x => x.ArtistUuid)
            .Should()
            .BeEquivalentTo([artist.uuid, other.uuid]);
        result.Performance.Reason.Should().Be("missing_setlist_assignment");
    }

    [Test]
    public async Task ChangedStoredAssignmentMakesAdmittedWorkStale()
    {
        var artist = await NewArtist();
        var prefix = "assignment-change-" + Guid.NewGuid();
        await Map(artist, prefix);
        var setlist = await ImportedSetlist(artist);
        var early = await Show(artist, "early");
        var late = await Show(artist, "late");
        var performances = new ShowPerformanceService(fixture.Db);
        await performances.AssignSetlistAsync(artist, setlist.id, early);
        var input = await Capture(prefix);
        var original = await bindings.BindSetlistAsync(input, Decode(input));
        var ticket = await Begin(original);
        await performances.AssignSetlistAsync(artist, setlist.id, late);

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await bindings.BindSetlistAsync(input, Decode(input)))
            .Performance.Value!.ShowUuid.Should()
            .Be(late);
    }

    [TestCase("missing-separator")]
    [TestCase("/missing-prefix")]
    [TestCase("missing-show/")]
    public async Task MalformedMirrorIdentityCannotSelectACatalog(string key)
    {
        var input = await CaptureResource(
            new("jerrygarcia.com", "mirror_show_page", key),
            SyntheticMirrorHtml
        );
        Func<Task> bind = () => bindings.BindSetlistAsync(input, Decode(input));
        await bind.Should().ThrowAsync<ArgumentException>();
    }

    private Task<NormalizationWorkTicket> Begin(NormalizationSetlistBindingSnapshot snapshot) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "setlist", Guid.NewGuid()),
            "synthetic-jerry-binding-test",
            snapshot.Inputs.Select(x => x.Resource).ToArray(),
            [],
            catalogInputs: snapshot.CatalogInputs,
            mappingRegistry: snapshot.MappingRegistry,
            expectedInputRevisions: snapshot.Inputs
        );

    private static NormalizationResourceKey Resource(string prefix) =>
        new("jerrygarcia.com", "mirror_show_page", prefix + "/" + ShowIdentifier);

    private Task<NormalizationInputRevision> Capture(
        string prefix,
        string html = SyntheticMirrorHtml
    ) => CaptureResource(Resource(prefix), html);

    private async Task<NormalizationInputRevision> CaptureResource(
        NormalizationResourceKey resource,
        string html
    ) =>
        (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    Encoding.UTF8.GetBytes(html),
                    "text/html; charset=utf-8",
                    "jerry-mirror-html:1",
                    0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;

    private static ProviderSetlistObservation Decode(NormalizationInputRevision input) =>
        new NormalizationProviderDecoder().Decode(input).Setlists.Single();

    private Task<Artist> NewArtist() =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var artist = await c.QuerySingleAsync<Artist>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Synthetic Jerry binder',@key,@uuid,now()) RETURNING *",
                new { key = Guid.NewGuid().ToString(), uuid = Guid.NewGuid() }
            );
            await c.ExecuteAsync("INSERT INTO features(artist_id) VALUES(@id)", new { artist.id });
            artist.features = new();
            return artist;
        });

    private Task Map(Artist artist, string prefix, string providerName = "jerrygarcia.com") =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name=@providerName ORDER BY id LIMIT 1",
                    new { providerName }
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),@providerName,'https://provider.invalid') RETURNING id",
                    new { providerName }
                );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@id,@provider,@prefix)",
                new
                {
                    artist.id,
                    provider,
                    prefix,
                }
            );
        });

    private async Task<SetlistShow> ImportedSetlist(Artist artist)
    {
        var venue = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO venues(artist_id,name,location,slug,upstream_identifier,uuid,updated_at) VALUES(@id,'Synthetic Jerry Hall','Veneta, OR, USA',@key,@key,@uuid,now()) RETURNING id",
                new
                {
                    artist.id,
                    key = Guid.NewGuid().ToString(),
                    uuid = Guid.NewGuid(),
                }
            )
        );
        return await new SetlistShowService(fixture.Db).Save(
            new()
            {
                artist_id = artist.id,
                venue_id = venue,
                date = new(1972, 8, 27),
                upstream_identifier = ShowIdentifier,
                updated_at = DateTime.UtcNow,
            }
        );
    }

    private Task<Guid> Show(Artist artist, string label) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO shows(artist_id,date,display_date,performance_key,performance_type,is_legacy_survivor,uuid,updated_at) VALUES(@id,'1972-08-27','1972-08-27',@key,@label,false,@uuid,now()) RETURNING uuid",
                new
                {
                    artist.id,
                    key = "synthetic-" + Guid.NewGuid(),
                    label,
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private sealed class SyntheticMirrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SyntheticMirrorHtml, Encoding.UTF8, "text/html"),
                }
            );
    }
}
