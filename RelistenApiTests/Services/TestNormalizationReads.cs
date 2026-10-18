using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Public reads exercise actual accepted state, input freshness and current playback joins.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationReads
{
    private DbService db = null!;
    private NormalizationReadService reads = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        reads = new NormalizationReadService(
            db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task EqualNamesRemainSeparateAndCursorBindsRouteAndFilter()
    {
        var name = "Alias " + Guid.NewGuid().ToString("N");
        var a = await Person(name);
        var b = await Person(name);
        var first = await reads.ListPeopleAsync(name, 1);
        first.items.Should().ContainSingle();
        first.next_cursor.Should().NotBeNull();
        var second = await reads.ListPeopleAsync(name, 1, first.next_cursor);
        first.items.Concat(second.items).Select(p => p.uuid).Should().BeEquivalentTo([a, b]);
        second.next_cursor.Should().BeNull();
        var route = () => reads.ListVenuesAsync(name, 1, first.next_cursor);
        var filter = () => reads.ListPeopleAsync("different", 1, first.next_cursor);
        await route.Should().ThrowAsync<ArgumentException>();
        await filter.Should().ThrowAsync<ArgumentException>();
        var invalid = () => reads.ListPeopleAsync("😀", 1);
        await invalid.Should().ThrowAsync<ArgumentException>();
        (await reads.ListPeopleAsync("😀😀")).items.Should().BeEmpty();
    }

    [Test]
    public async Task MergeFetchReturnsSurvivorAndDoesNotListRetiredIdentity()
    {
        var key = "Redirect " + Guid.NewGuid().ToString("N");
        var retired = await Person(key + " old");
        var survivor = await Person(key + " new");
        using var domain = new NormalizationDomainFixture();
        var input = await domain.Capture($"Old handle; {key}", "people");
        // Reads need a supported assertion, not just a row in the raw evidence table.
        (
            await domain.PublishPeople(
                new NormalizedPeopleService(domain.Db, domain.Work),
                input,
                new(
                    [],
                    [new(Guid.NewGuid(), retired, "Old handle", input.Reference)],
                    [new(retired, "test", "handle", key, input.Reference)],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        await Sql(
            "INSERT INTO normalization.person_redirects VALUES(@retired,@survivor,@decision)",
            new
            {
                retired,
                survivor,
                decision = Guid.NewGuid(),
            }
        );
        var person = (await reads.GetPersonAsync(retired))!;
        person.uuid.Should().Be(survivor);
        person.requested_uuid.Should().Be(retired);
        person.redirect_uuid.Should().Be(survivor);
        person.aliases.Should().Contain("Old handle");
        var externalIdentity = person.external_identities.Should().ContainSingle().Subject;
        externalIdentity.provider_key.Should().Be(key);
        externalIdentity.basis.Should().Be("provider_assertion");
        externalIdentity.revision.Should().Be(1);
        (await reads.ListPeopleAsync(key + " old"))
            .items.Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(survivor);
        (await reads.ListPeopleAsync(key)).items.Select(p => p.uuid).Should().Equal(survivor);
        (await reads.ListPersonRecordingsAsync(retired))!.items.Should().BeEmpty();
        (await reads.GetPersonAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Test]
    public async Task CrossArtistRecordingsDeduplicateCreditsAndTrackCurrentCatalogMoves()
    {
        var person = await Person("Cross artist " + Guid.NewGuid().ToString("N"));
        var first = await Recording("1997-xx-xx");
        var second = await Recording("2001-01-01");
        var taper = await Credit(first.Source, person, "taper");
        var transfer = await Credit(first.Source, person, "transferrer");
        await Credit(second.Source, person, "taper");
        var page = (await reads.ListPersonRecordingsAsync(person))!;
        page.items.Select(r => r.source_uuid).Should().Equal(second.Source, first.Source);
        var result = page.items.Single(r => r.source_uuid == first.Source);
        result.roles.Should().BeEquivalentTo(["taper", "transferrer"]);
        result.credit_uuids.Should().BeEquivalentTo([taper, transfer]);
        result.track_count.Should().Be(1);
        (await reads.ListPersonRecordingsAsync(person, "transferrer"))!
            .items.Should()
            .ContainSingle();
        (await reads.ListPersonRecordingsAsync(person, artistUuid: first.Artist))!
            .items.Should()
            .ContainSingle();
        var unknown = () => reads.ListPersonRecordingsAsync(person, artistUuid: Guid.NewGuid());
        await unknown.Should().ThrowAsync<NormalizationEntityNotFoundException>();
        await Sql(
            "UPDATE source_tracks SET is_orphaned=true WHERE source_id=(SELECT id FROM sources WHERE uuid=@source)",
            new { source = first.Source }
        );
        (await reads.ListPersonRecordingsAsync(person))!
            .items.Select(r => r.source_uuid)
            .Should()
            .Equal(second.Source);
        await Sql("DELETE FROM sources WHERE uuid=@source", new { source = second.Source });
        (await reads.ListPersonRecordingsAsync(person))!.items.Should().BeEmpty();
        (await reads.ListRecordingCreditsAsync(second.Source))!
            .items.Should()
            .ContainSingle()
            .Which.reference_available.Should()
            .BeFalse();
    }

    [Test]
    public async Task VenueOverrideWinsAndSiteDoesNotIncludeRoom()
    {
        var name = "Hall " + Guid.NewGuid().ToString("N");
        var site = await Venue(name);
        var room = await Venue("Room " + name, site);
        var recording = await Recording("1980-xx-xx");
        await Sql(
            """
            INSERT INTO normalization.imported_venue_assignments(uuid,imported_venue_uuid,canonical_venue_uuid,state,basis,reason_code,decision_uuid)
            VALUES(@assignment,@legacy,@site,'accepted','provider_assertion','explicit',@decision)
            """,
            new
            {
                assignment = Guid.NewGuid(),
                legacy = recording.Venue,
                site,
                decision = Guid.NewGuid(),
            }
        );
        var shows = (await reads.ListVenueShowsAsync(site))!;
        shows.items.Should().ContainSingle().Which.match_evidence.Should().Equal("imported_venue");
        await Sql(
            """
            INSERT INTO normalization.show_venue_assignments(uuid,show_uuid,canonical_venue_uuid,state,basis,reason_code,decision_uuid)
            VALUES(@assignment,@show,@room,'accepted','reviewed','correction',@decision)
            """,
            new
            {
                assignment = Guid.NewGuid(),
                show = recording.Show,
                room,
                decision = Guid.NewGuid(),
            }
        );
        (await reads.ListVenueShowsAsync(site))!.items.Should().BeEmpty();
        var corrected = (await reads.ListVenueShowsAsync(room))!
            .items.Should()
            .ContainSingle()
            .Subject;
        corrected.show_uuid.Should().Be(recording.Show);
        corrected.match_evidence.Should().Equal("show_venue");
        corrected.decision_basis.Should().Equal("reviewed");
        corrected.display_date.Should().Be("1980-xx-xx");
    }

    [Test]
    public async Task VenueRoomDetailUsesCurrentParentAfterSiteMerge()
    {
        var name = "Merged hall " + Guid.NewGuid().ToString("N");
        var oldSite = await Venue(name + " old");
        var currentSite = await Venue(name + " current");
        var room = await Venue(name + " room", oldSite);
        await Sql(
            "INSERT INTO normalization.venue_redirects VALUES(@oldSite,@currentSite,@decision)",
            new
            {
                oldSite,
                currentSite,
                decision = Guid.NewGuid(),
            }
        );
        (await reads.GetVenueAsync(room))!.parent_venue_uuid.Should().Be(currentSite);
        (await reads.ListVenuesAsync(name + " room"))
            .items.Should()
            .ContainSingle()
            .Which.parent_venue_uuid.Should()
            .Be(currentSite);
    }

    [Test]
    public async Task CurrentCreditEvidenceExcludesPriorDecisionAndReportsChangedInput()
    {
        var person = await Person("Evidence " + Guid.NewGuid().ToString("N"));
        var recording = await Recording("2000-01-01");
        var credit = await Credit(recording.Source, person, "taper");
        var inputs = new NormalizationEvidenceService(db);
        var resource = new NormalizationResourceKey(
            "test",
            "read-evidence",
            Guid.NewGuid().ToString()
        );
        var capture = await inputs.CaptureInputAsync(
            new NormalizationInputCapture(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes("{\"taper\":\"Pat\"}"),
                "application/json",
                "v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var revision = (await inputs.GetCurrentInputAsync(resource))!.Revision.Uuid;
        await Sql(
            """
            INSERT INTO normalization.recording_credit_evidence(credit_uuid,credit_revision,input_revision_uuid,json_pointer,quote)
            VALUES(@credit,1,@revision,'/taper','Pat'),(@credit,0,@revision,'/old','not current')
            """,
            new { credit, revision }
        );
        var result = (await reads.ListRecordingCreditsAsync(recording.Source))!.items.Single();
        result.evidence.Should().ContainSingle().Which.quote.Should().Be("Pat");
        result.evidence_status.Should().Be("current");
        await inputs.CaptureInputAsync(
            new NormalizationInputCapture(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes("{\"taper\":\"Changed\"}"),
                "application/json",
                "v1",
                1,
                DateTimeOffset.UtcNow
            )
        );
        result = (await reads.ListRecordingCreditsAsync(recording.Source))!.items.Single();
        result.evidence_status.Should().Be("changed");
        result.evidence.Single().quote.Should().Be("Pat");
    }

    [Test]
    public async Task HttpReadsCrossArtistPlaybackAndDistinguishesMissingFromKnownEmpty()
    {
        var person = await Person("HTTP " + Guid.NewGuid().ToString("N"));
        var empty = await Person("Empty " + Guid.NewGuid().ToString("N"));
        var first = await Recording("2000-xx-xx");
        var second = await Recording("2001-xx-xx");
        await Credit(first.Source, person, "taper");
        await Credit(second.Source, person, "transferrer");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationPeopleVenuesController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        using var result = await client.GetAsync(
            $"/api/v3/normalization/people/{person}/recordings"
        );
        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await result.Content.ReadAsStringAsync());
        json["items"]!
            .Select(item => Guid.Parse(item["source_uuid"]!.Value<string>()!))
            .Should()
            .Equal(second.Source, first.Source);
        result.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var missing = await client.GetAsync(
            $"/api/v3/normalization/people/{Guid.NewGuid()}/recordings"
        );
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var known = await client.GetAsync($"/api/v3/normalization/people/{empty}/recordings");
        known.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject.Parse(await known.Content.ReadAsStringAsync())["items"]!.Should().BeEmpty();
        await app.StopAsync();
    }

    private Task<int> Sql(string sql, object? args = null) =>
        db.WithWriteConnection(c => c.ExecuteAsync(sql, args));

    private async Task<Guid> Person(string name)
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@uuid,@name,@key,'person')",
            new
            {
                uuid,
                name,
                key = NormalizedPeopleService.NameKey(name),
            }
        );
        return uuid;
    }

    private async Task<Guid> Venue(string name, Guid? parent = null)
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind,parent_venue_uuid) VALUES(@uuid,@name,@key,@kind,@parent)",
            new
            {
                uuid,
                name,
                key = NormalizedPeopleService.NameKey(name),
                kind = parent == null ? "site" : "room",
                parent,
            }
        );
        return uuid;
    }

    private async Task<Guid> Credit(Guid source, Guid person, string role)
    {
        var uuid = Guid.NewGuid();
        await Sql(
            "INSERT INTO normalization.recording_credits(uuid,source_uuid,person_uuid,role,original_text,state,basis,reason_code,decision_uuid) VALUES(@uuid,@source,@person,@role,'Pat','accepted','provider_assertion','explicit',@decision)",
            new
            {
                uuid,
                source,
                person,
                role,
                decision = Guid.NewGuid(),
            }
        );
        return uuid;
    }

    private sealed record RecordingFixture(Guid Artist, Guid Source, Guid Show, Guid Venue);

    private async Task<RecordingFixture> Recording(string literalDate)
    {
        var fixture = new RecordingFixture(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        await Sql(
            """
            INSERT INTO artists(uuid,name,slug,updated_at) VALUES(@Artist,'Read fixture',@slug,now());
            INSERT INTO venues(uuid,artist_id,name,updated_at) SELECT @Venue,id,'Imported hall',now() FROM artists WHERE uuid=@Artist;
            INSERT INTO shows(uuid,artist_id,venue_id,date,display_date,updated_at)
                SELECT @Show,a.id,v.id,'2000-01-01',@literalDate,now() FROM artists a JOIN venues v ON v.artist_id=a.id WHERE a.uuid=@Artist;
            INSERT INTO sources(uuid,show_id,artist_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
                SELECT @Source,s.id,s.artist_id,false,false,false,@slug,@literalDate,now() FROM shows s WHERE uuid=@Show;
            INSERT INTO source_sets(uuid,source_id,name,updated_at) SELECT @setUuid,id,'Set 1',now() FROM sources WHERE uuid=@Source;
            INSERT INTO source_tracks(uuid,source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,duration,updated_at)
                SELECT @trackUuid,s.id,sets.id,s.artist_id,1,'Song','song',@url,120,now()
                FROM sources s JOIN source_sets sets ON sets.source_id=s.id WHERE s.uuid=@Source;
            """,
            new
            {
                fixture.Artist,
                fixture.Source,
                fixture.Show,
                fixture.Venue,
                literalDate,
                slug = fixture.Source.ToString(),
                setUuid = Guid.NewGuid(),
                trackUuid = Guid.NewGuid(),
                url = "https://example.test/" + fixture.Source + ".mp3",
            }
        );
        return fixture;
    }
}
