using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Import.PhishNet;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishNet;
using RelistenApiTests.Ingestion;
using RelistenApiTests.Services;

namespace RelistenApiTests.Importers;

/// <summary>Provider adapters retain response evidence before their catalog projections discard fields.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationEnrichmentCapture
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SetlistPagePreservesCoverGuestAndInfoBeforeTolerantProjection()
    {
        var context = await Context();
        var body = JObject.Parse(
            ProviderFixtures.Response("setlist.fm", new("https://fixture/setlists"))
        );
        var song = (JObject)body["setlist"]![0]!["sets"]!["set"]![0]!["song"]![0]!;
        song["cover"] = new JObject { ["name"] = "Cover artist", ["mbid"] = "cover-id" };
        song["with"] = new JObject { ["name"] = "Guest player" };
        song["info"] = "Opening quote > reprise";
        using var handler = new ResponseHandler(body.ToString());
        using var http = new HttpClient(handler);
        var importer = SetlistImporter();
        importer.UseIngestionHttpClient(http);

        var page = await importer.ReadSetlistPageAsync(context, null);

        page.Items.Should().HaveCount(2);
        page.Items[0].Input.sets.set[0].song[0].name.Should().Be("Fixture Song");
        var retained = await evidence.GetCurrentInputAsync(SetlistResource(context));
        retained!.Revision.Payload.Should().Equal(handler.Payload);
        var rawSong = JObject.Parse(Encoding.UTF8.GetString(retained.Revision.Payload))["setlist"]![
            0
        ]!["sets"]!["set"]![0]!["song"]![0]!;
        rawSong["cover"]!["mbid"]!.Value<string>().Should().Be("cover-id");
        rawSong["with"]!["name"]!.Value<string>().Should().Be("Guest player");
        rawSong["info"]!.Value<string>().Should().Be("Opening quote > reprise");
        retained.Revision.MediaType.Should().Be("application/json; charset=utf-8");
        handler.Requests.Should().Be(1);
        // Header credentials belong to HTTP, never to a retained route identity.
        SetlistResource(context).ProviderKey.Should().NotContain(handler.ApiKey!);
    }

    [Test]
    public async Task InvalidSetlistPaginationCannotReplaceTheLastValidPage()
    {
        var context = await Context();
        using var handler = new ResponseHandler(
            ProviderFixtures.Response("setlist.fm", new("https://fixture/setlists"))
        );
        using var http = new HttpClient(handler);
        var importer = SetlistImporter();
        importer.UseIngestionHttpClient(http);
        await importer.ReadSetlistPageAsync(context, null);
        var prior = await evidence.GetCurrentInputAsync(SetlistResource(context));
        handler.SetBody("{\"page\":0,\"itemsPerPage\":20,\"total\":0,\"setlist\":[]}");

        var invalid = () => importer.ReadSetlistPageAsync(context, null);
        await invalid.Should().ThrowAsync<InvalidDataException>();

        (await evidence.GetCurrentInputAsync(SetlistResource(context)))!
            .Revision.Uuid.Should()
            .Be(prior!.Revision.Uuid);
        (await evidence.GetFetchHistoryAsync(SetlistResource(context))).Should().ContainSingle();
    }

    [Test]
    public async Task DisabledSetlistCaptureKeepsOneHttpRequestAndNoEvidence()
    {
        var context = await Context();
        using var handler = new ResponseHandler(
            ProviderFixtures.Response("setlist.fm", new("https://fixture/setlists"))
        );
        using var http = new HttpClient(handler);
        var importer = SetlistImporter(false);
        importer.UseIngestionHttpClient(http);
        (await importer.ReadSetlistPageAsync(context, null)).Items.Should().HaveCount(2);
        handler.Requests.Should().Be(1);
        (await evidence.GetCurrentInputAsync(SetlistResource(context))).Should().BeNull();
    }

    [Test]
    public async Task PhantasyDetailEvidenceSurvivesCatalogRollback()
    {
        var context = await Context();
        var body = JObject.Parse(
            ProviderFixtures.Response("phantasytour.com", new("https://fixture/shows/1/blob"))
        );
        body["data"]!["credits"] = "Setlist compiled by Fixture Person";
        body["data"]!["footnotes"] = new JArray(
            new JObject { ["id"] = 1, ["note"] = "Guest on guitar" }
        );
        body["data"]!["sets"]![0]!["songs"]![0]!["segue"] = true;
        body["data"]!["sets"]![0]!["songs"]![0]!["footnotes"] = new JArray(1);
        using var handler = new ResponseHandler(body.ToString());
        using var http = new HttpClient(handler);
        var importer = PhantasyImporter();
        importer.UseIngestionHttpClient(http);
        // Force catalog projection to fail after writing a venue. Retention has
        // already committed independently of this catalog transaction.
        context.Artist.features = null!;
        var item = new DiscoveredItem<
            PhantasyTourShowId,
            Relisten.Vendor.PhantasyTour.PhantasyTourShowListing
        >(new(1), new() { id = 1 });
        var apply = () => importer.ImportShowAsync(context, item);

        await apply.Should().ThrowAsync<NullReferenceException>();

        var retained = await evidence.GetCurrentInputAsync(new("phantasytour.com", "show", "1"));
        retained!.Revision.Payload.Should().Equal(handler.Payload);
        var raw = JObject.Parse(Encoding.UTF8.GetString(retained.Revision.Payload));
        raw["data"]!["credits"]!.Value<string>().Should().Be("Setlist compiled by Fixture Person");
        raw["data"]!["footnotes"]![0]!["note"]!.Value<string>().Should().Be("Guest on guitar");
        raw["data"]!["sets"]![0]!["songs"]![0]!["segue"]!.Value<bool>().Should().BeTrue();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM setlist_shows WHERE artist_id=@id",
                    new { id = context.Artist.id }
                )
            )
        )
            .Should()
            .Be(0);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM venues WHERE artist_id=@id",
                    new { id = context.Artist.id }
                )
            )
        )
            .Should()
            .Be(0);
        handler.Requests.Should().Be(1);
    }

    [Test]
    public async Task InvalidPhantasyDetailCannotReplaceTheLastValidResponse()
    {
        var context = await Context();
        using var handler = new ResponseHandler(
            ProviderFixtures.Response("phantasytour.com", new("https://fixture/shows/2/blob"))
        );
        using var http = new HttpClient(handler);
        var importer = PhantasyImporter();
        importer.UseIngestionHttpClient(http);
        var item = new DiscoveredItem<
            PhantasyTourShowId,
            Relisten.Vendor.PhantasyTour.PhantasyTourShowListing
        >(new(2), new() { id = 2 });
        await importer.ImportShowAsync(context, item);
        var resource = new NormalizationResourceKey("phantasytour.com", "show", "2");
        var prior = await evidence.GetCurrentInputAsync(resource);
        // Retry under a new artist so the adapter fetches details instead of
        // recovering the already committed setlist for the first artist.
        var nextContext = await Context();
        handler.SetBody("{\"data\":{\"id\":99,\"dateTime\":\"2000-01-02T20:00:00Z\"}}");
        var invalid = () => importer.ImportShowAsync(nextContext, item);

        await invalid.Should().ThrowAsync<InvalidDataException>();

        (await evidence.GetCurrentInputAsync(resource))!
            .Revision.Uuid.Should()
            .Be(prior!.Revision.Uuid);
        (await evidence.GetFetchHistoryAsync(resource)).Should().ContainSingle();
        handler.Requests.Should().Be(2);
    }

    [Test]
    public async Task PhishNetRetainsOriginalBytesBeforeStringRepairAndArtistFiltering()
    {
        var marker = Guid.NewGuid().ToString();
        var corrupt = "Â\u009d";
        var body =
            $$"""{"error":false,"unknown":"{{marker}}","data":[{"artist_name":"Phish","setlist_notes":"a{{corrupt}}b"},{"artist_name":"Other","showdate":"2000-01-01"}]}""";
        using var handler = new ResponseHandler(body);
        using var http = new HttpClient(handler);
        var shows = await new PhishNetApiClient(http, Capture()).Shows(null);

        shows.Should().ContainSingle().Which.setlist_notes.Should().Be("ab");
        var retained = await evidence.GetCurrentInputAsync(new("phish.net", "shows", "v5"));
        retained!.Revision.Payload.Should().Equal(handler.Payload);
        JObject.Parse(Encoding.UTF8.GetString(retained.Revision.Payload))["data"]!
            .Should()
            .HaveCount(2);
        retained.Resource.ProviderKey.Should().Be("v5");
        retained.Resource.ProviderKey.Should().NotContain(handler.QuerySecret!);
        handler.Requests.Should().Be(1);
    }

    [Test]
    public async Task PhishNetInvalidEnvelopeCannotReplaceCurrentEvidence()
    {
        using var handler = new ResponseHandler("{\"error\":false,\"data\":[]}");
        using var http = new HttpClient(handler);
        var client = new PhishNetApiClient(http, Capture());
        await client.Shows(null);
        var resource = new NormalizationResourceKey("phish.net", "shows", "v5");
        var prior = await evidence.GetCurrentInputAsync(resource);
        handler.SetBody("{\"error\":true,\"data\":[]}");
        var invalid = () => client.Shows(null);
        await invalid.Should().ThrowAsync<InvalidDataException>();
        (await evidence.GetCurrentInputAsync(resource))!
            .Revision.Uuid.Should()
            .Be(prior!.Revision.Uuid);
    }

    [Test]
    public async Task PhishNetRatingsCaptureRetainsTheCompleteHtmlFixture()
    {
        var payload = await File.ReadAllBytesAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                "setlist-1992-11-21.html"
            )
        );
        using var handler = new ResponseHandler(payload, "text/html");
        using var http = new HttpClient(handler);
        var result = await new PhishNetRatingsScraper(
            http,
            "1992-11-21",
            Capture()
        ).ScrapeRatings();
        result.RatingMatched.Should().BeTrue();
        (await evidence.GetCurrentInputAsync(new("phish.net", "ratings", "1992-11-21")))!
            .Revision.Payload.Should()
            .Equal(payload);
        handler.Requests.Should().Be(1);
    }

    [Test]
    public async Task UnmatchedSuccessfulRatingsPageRetainsEvidenceAndPreservesPublishedRatingsAndReviews()
    {
        var source = await fixture.Source();
        var reviewUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var show = await c.QuerySingleAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,updated_at,uuid) VALUES(@ArtistId,'2026-10-17','2026-10-17',now(),@uuid) RETURNING id",
                new { source.ArtistId, uuid = Guid.NewGuid() }
            );
            await c.ExecuteAsync(
                """
                INSERT INTO upstream_sources(id,name,url) VALUES(3,'phish.net','https://phish.net') ON CONFLICT(id) DO NOTHING;
                UPDATE sources SET show_id=@show,avg_rating=7.5,num_ratings=9,num_reviews=1 WHERE id=@SourceId;
                INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date) VALUES(@SourceId,@show,'legacy','2026-10-17');
                INSERT INTO source_reviews(source_id,review,author,updated_at,uuid) VALUES(@SourceId,'Published review','Reviewer',now(),@reviewUuid);
                """,
                new
                {
                    source.SourceId,
                    show,
                    reviewUuid,
                }
            );
        });
        var context = new ProviderContext(
            new Artist { id = source.ArtistId, features = new Features() },
            new ArtistUpstreamSource { upstream_source_id = 3 },
            new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddMinutes(5)),
            new(fixture.Db),
            CancellationToken.None
        );
        using var handler = new ResponseHandler(
            "<html><body>Setlist notes without a ratings widget</body></html>"
        );
        using var http = new HttpClient(handler);
        var importer = new PhishNetImporter(
            null!,
            new(fixture.Db, null!),
            new(fixture.Db),
            new(fixture.Db),
            NullLogger<PhishNetImporter>.Instance,
            new(fixture.Db),
            new(fixture.Db),
            Capture()
        );
        importer.UseIngestionHttpClient(http);

        var result = await importer.EnrichSourceAsync(
            context,
            new(new(source.SourceId), new PhishNetSourceInput(null, SourceDate: "2026-10-17"))
        );

        result.Disposition.Should().Be(ItemDisposition.Applied);
        var current = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Source>(
                "SELECT * FROM sources WHERE id=@SourceId",
                new { source.SourceId }
            )
        );
        current.avg_rating.Should().Be(7.5);
        current.num_ratings.Should().Be(9);
        current.num_reviews.Should().Be(1);
        var review = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<SourceReview>(
                "SELECT * FROM source_reviews WHERE source_id=@SourceId",
                new { source.SourceId }
            )
        );
        review.uuid.Should().Be(reviewUuid);
        review.review.Should().Be("Published review");
        (await evidence.GetCurrentInputAsync(new("phish.net", "ratings", "2026-10-17")))!
            .Revision.Payload.Should()
            .Equal(handler.Payload);
        handler.Requests.Should().Be(1);
    }

    private NormalizationProviderInputService Capture(bool enabled = true) =>
        new(
            evidence,
            Options.Create(new NormalizationProviderCaptureOptions { Enabled = enabled })
        );

    private SetlistFmImporter SetlistImporter(bool enabled = true) =>
        new(
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<SetlistFmImporter>.Instance,
            null!,
            Capture(enabled)
        );

    private PhantasyTourImporter PhantasyImporter() =>
        new(
            null!,
            NullLogger<PhantasyTourImporter>.Instance,
            new(fixture.Db, null!),
            new(fixture.Db, null!),
            new(fixture.Db),
            new(fixture.Db),
            Capture()
        );

    private async Task<ProviderContext> Context()
    {
        var source = await fixture.Source();
        return new(
            new Artist
            {
                id = source.ArtistId,
                uuid = Guid.NewGuid(),
                musicbrainz_id = Guid.NewGuid().ToString(),
                features = new Features(),
            },
            new ArtistUpstreamSource { upstream_identifier = "1" },
            new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddMinutes(5)),
            new(fixture.Db),
            CancellationToken.None
        );
    }

    private static NormalizationResourceKey SetlistResource(ProviderContext context) =>
        new("setlist.fm", "setlist-page", $"{context.Artist.musicbrainz_id}:page=1");

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public byte[] Payload { get; private set; }
        private readonly string mediaType;
        public int Requests { get; private set; }
        public string? ApiKey { get; private set; }
        public string? QuerySecret { get; private set; }

        public ResponseHandler(string body)
            : this(Encoding.UTF8.GetBytes(body), "application/json") { }

        public ResponseHandler(byte[] payload, string mediaType)
        {
            Payload = payload;
            this.mediaType = mediaType;
        }

        public void SetBody(string body) => Payload = Encoding.UTF8.GetBytes(body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            ApiKey = request.Headers.TryGetValues("x-api-key", out var values)
                ? values.Single()
                : null;
            QuerySecret = request.RequestUri!.Query.Split("apikey=").Last();
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Payload)
                    {
                        Headers = { ContentType = new(mediaType) { CharSet = "utf-8" } },
                    },
                }
            );
        }
    }
}
