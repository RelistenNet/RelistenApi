using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Api.V4;
using Relisten.Controllers.V4;
using Relisten.Identifiers;
using Relisten.Search;
using RelistenApiTests.Popularity;
using RelistenApiTests.V4;

namespace RelistenApiTests.Search;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestCatalogSearchHttp
{
    private const string SearchPath = "/api/v4/search";
    private const string CatalogServiceKey = "search-http-fixture";
    private const string TypesenseUrl = "http://127.0.0.1:18108";
    private const string TypesenseKey = "local_dev_typesense";
    private const int PagingShowCount = 20;
    private const string JapanShowsQuery = "?q=Pagination%20Japan&scope=shows";
    private const string ScarletSongsQuery = "?q=Scarlet%20Begonias&scope=songs&typing=true";
    private const string PlayedScarlet = "perf_scarlet_most_played";
    private readonly string _prefix = "test_search_http_" + Guid.NewGuid().ToString("N");
    private readonly Guid _review = Guid.NewGuid();
    private string _previousPrimary = null!;
    private string _previousReplica = null!;
    private bool _seededReview;
    private HttpClient? _typesenseHttp;
    private TypesenseClient? _typesense;
    private WebApplication? _app;
    private HttpClient _http = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        _previousPrimary = DbService.ConnStr;
        _previousReplica = DbService.ReadOnlyConnStr;
        MigratedTestDatabase.RequireLocalDatabase();

        await BuildSearchIndex();
        await StartSearchApi();
    }

    private async Task BuildSearchIndex()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await V4CatalogFixture.EnsureLoaded(connection);
        await connection.ExecuteAsync(
            """
            INSERT INTO source_reviews(uuid, source_id, title, review, updated_at)
            VALUES (@uuid, 910004, 'A remarkable encore', 'The encore was a highlight.', now())
            """,
            new { uuid = _review }
        );
        _seededReview = true;

        _typesenseHttp = new HttpClient();
        TypesenseClient.Configure(_typesenseHttp, TypesenseUrl, TypesenseKey);
        _typesense = new TypesenseClient(_typesenseHttp);
        var index = new SearchIndexBuilder(_typesense, NullLogger<SearchIndexBuilder>.Instance);
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "UPDATE sources SET avg_rating = 8.125, num_reviews = 3 WHERE id = 910004",
            transaction: transaction
        );
        await SeedShowAfterLastPopularityRefresh(connection, transaction);
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, transaction),
            [],
            CancellationToken.None
        );

        var previewUuid = Guid.Parse("00000000-0000-0000-0000-000000000044");
        var artistUuid = Guid.Parse("00000000-0000-0000-0000-000000000014");
        var resourceProjection = new CatalogSearchResourceProjection(
            Relisten.Catalog.CatalogReadSession.Borrow(connection, transaction),
            NullLoggerFactory.Instance
        );
        var tracks = await resourceProjection.Tracks(
            [previewUuid],
            new HashSet<Guid> { artistUuid },
            CancellationToken.None
        );
        var passageTrack = PassageTrack(
            CatalogSearchResourceProjection.Serialize(
                V4CatalogProjection.Track(tracks[previewUuid])
            )
        );
        // These exact song matches fill the first unscoped page. The less exact
        // venue match only survives if the scope limits candidates in Typesense.
        var distractors = Enumerable
            .Range(0, 24)
            .Select(i => new Dictionary<string, object>
            {
                ["id"] = $"distractor_{i}",
                ["kind"] = "song",
                ["name"] = "current",
                ["label"] = "Current",
                ["country"] = "US",
                ["popularity"] = 1,
                ["artist_slug"] = "v4-review-4",
                ["display"] = new { id = $"distractor_{i}", name = "Current" },
            });
        // Each upstream page can include both shows and performance groups.
        // The first combined page is large enough to detect truncation after paging.
        var showNumbers = Enumerable.Range(1, PagingShowCount);
        var pageShows = showNumbers.Select(number =>
            PagingDocument(Id("show", 1_000 + number), "show", number)
        );
        var pagePerformances = showNumbers.SelectMany(number =>
            Enumerable.Range(1, 3).Select(cut => PagingPerformance(number, cut))
        );
        var scarletPerformances = Enumerable
            .Range(1, 20)
            .Select(number => ScarletPerformance(number, played: false))
            .Append(ScarletPerformance(21, played: true));
        var documents = projection.Documents(CancellationToken.None);
        await index.Build(
            _prefix,
            Guid.NewGuid().ToString("N"),
            documents with
            {
                Entities = documents.Entities.Concat(
                    distractors
                        .Append(passageTrack)
                        .Concat(pageShows)
                        .Append(PaginationOutsideJapan())
                        .Concat(Enumerable.Range(1, 21).Select(ScarletParentShow))
                        .Append(PartialDisplayProbe())
                        .Append(RenderableProbe())
                        .Append(RecordingParentShow(18803, 0))
                        .Append(RecordingParentShow(18805, 0))
                        .Append(ReviewFixtureShow())
                        .Append(RecordingParentShow(18700, 41061))
                        .Append(RecordingParentShow(18710, 75))
                        .ToAsyncEnumerable()
                ),
                Recordings = documents.Recordings.Concat(
                    Enumerable
                        .Range(1, 5)
                        .Select(number => MatchingRecording(18700 + number, 18700, 12L))
                        .Append(MatchingRecording(18711, 18710, 70L))
                        .Append(MatchingRecording(18802, 18802, 0, "Review tape"))
                        .Append(MatchingRecording(18803, 18803, 0, "Review tape"))
                        .Append(MatchingRecording(18805, 18805, 0, "Review tape"))
                        .Concat(Enumerable.Range(19000, 28).Select(PhiladelphiaRecording))
                        .Append(MatchingRecording(19028, 4, 20000, "Philly Archive"))
                        .ToAsyncEnumerable()
                ),
                Performances = documents
                    .Performances.Concat(
                        pagePerformances.Concat(scarletPerformances).ToAsyncEnumerable()
                    )
                    .Select(document =>
                    {
                        if (
                            (string?)document.GetValueOrDefault("preview_track_id")
                            == Id("track", 44)
                        )
                        {
                            document["aliases"] = "medley fixture";
                        }
                        return document;
                    }),
                Reviews = documents.Reviews.Concat(
                    new[]
                    {
                        ReviewDocument(18801, "", "   "),
                        ReviewDocument(18802, "", "The encore sounded glorious."),
                        ReviewDocument(18803, "Wonderful tape", ""),
                        ReviewDocument(18805, "Philly Encore", "A tight second set."),
                    }.ToAsyncEnumerable()
                ),
            },
            CancellationToken.None
        );
    }

    private static Task SeedShowAfterLastPopularityRefresh(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction
    ) =>
        connection.ExecuteAsync(
            """
            INSERT INTO artists(uuid, name, slug, updated_at)
                VALUES ('00000000-0000-0000-0000-000000019943', 'Fresh show fixture',
                        'fresh-show-playback-fixture', now());
            INSERT INTO popularity_detail_publications(artist_uuid, generated_at)
                VALUES ('00000000-0000-0000-0000-000000019943', now() - interval '1 day')
                ON CONFLICT (artist_uuid) DO NOTHING;
            INSERT INTO shows(uuid, artist_id, date, display_date, updated_at)
                SELECT '00000000-0000-0000-0000-000000019944', id, '1996-03-15', '1996-03-15', now()
                FROM artists WHERE uuid='00000000-0000-0000-0000-000000019943';
            INSERT INTO sources(uuid, artist_id, show_id, is_soundboard, is_remaster, has_jamcharts,
                                upstream_identifier, display_date, updated_at)
                SELECT '00000000-0000-0000-0000-000000019945', artist_id, id, false, false, false,
                       'fresh-show-playback-fixture', display_date, now()
                FROM shows WHERE uuid='00000000-0000-0000-0000-000000019944';
            INSERT INTO source_sets(uuid, source_id, name, updated_at)
                SELECT '00000000-0000-0000-0000-000000019946', id, 'Set 1', now()
                FROM sources WHERE uuid='00000000-0000-0000-0000-000000019945';
            INSERT INTO source_tracks(uuid, artist_id, source_id, source_set_id, track_position, title, slug,
                                      mp3_url, duration, updated_at)
                SELECT '00000000-0000-0000-0000-000000019947', source.artist_id, source.id, set.id,
                       1, 'A Fresh Song', 'a-fresh-song', 'https://example.test/fresh-show.mp3', 60, now()
                FROM sources source JOIN source_sets set ON set.source_id=source.id
                WHERE source.uuid='00000000-0000-0000-0000-000000019945';
            """,
            transaction: transaction
        );

    private static Dictionary<string, object> RecordingParentShow(int id, long plays) =>
        new()
        {
            ["id"] = Id("show", id),
            ["kind"] = "show",
            ["name"] = "westside theater",
            ["artist_slug"] = "v4-review-4",
            ["display"] = new { id = Id("show", id) },
            ["display_popularity"] = new
            {
                windows = new Dictionary<string, object> { ["1y"] = new { plays } },
            },
            ["label"] = "Westside Theater",
            ["show_id"] = Id("show", id),
            ["track_plays"] = plays,
            ["ranking_signal"] = plays,
            ["popularity"] = plays,
        };

    private static Dictionary<string, object> MatchingRecording(
        int id,
        int show,
        long plays,
        string title = "Overflow recording probe"
    ) =>
        new()
        {
            ["id"] = Id("source", id),
            ["kind"] = "recording",
            ["name"] = SearchText.Fold(title),
            ["label"] = title,
            ["artist_slug"] = "v4-review-4",
            ["display"] = new { id = Id("source", id), title },
            ["show_id"] = Id("show", show),
            ["track_plays"] = plays,
            ["ranking_signal"] = plays,
            ["popularity"] = plays,
        };

    private static Dictionary<string, object> ReviewFixtureShow() =>
        new()
        {
            ["id"] = Id("show", 18802),
            ["kind"] = "show",
            ["name"] = "orchard feedback",
            ["artist_slug"] = "v4-review-4",
            ["display"] = new { id = Id("show", 18802) },
            ["label"] = "Orchard Feedback",
            ["place"] = "orchard feedback",
            ["date"] = 19980922,
            ["show_id"] = Id("show", 18802),
        };

    private static Dictionary<string, object> ReviewDocument(
        int suffix,
        string title,
        string text
    ) =>
        new()
        {
            ["id"] = Id("review", suffix),
            ["kind"] = "review",
            ["name"] = SearchText.Fold(title),
            ["label"] = title,
            ["review_text"] = text,
            ["artist_slug"] = "v4-review-4",
            ["display"] = new
            {
                id = Id("source_review", suffix),
                title,
                review = text,
            },
            ["place"] = "orchard feedback",
            ["date"] = 19980922,
            ["show_id"] = Id("show", suffix),
            ["source_id"] = Id("source", suffix),
        };

    private static Dictionary<string, object> PagingDocument(string id, string kind, int number) =>
        new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = "pagination",
            ["artist_slug"] = "v4-review-4",
            ["display"] = new { id = Id("show", 1_000 + number), title = "Pagination" },
            ["label"] = "Pagination",
            ["country"] = "JP",
            ["show_id"] = Id("show", 1_000 + number),
            ["popularity"] = number,
        };

    private static Dictionary<string, object> PagingPerformance(int number, int cut)
    {
        var sequence = number * 3 + cut;
        var document = PagingDocument($"perf_{sequence:x24}", "performance", number);
        var title = cut == 3 ? "Unrelated song" : "Pagination";
        document["name"] = title.ToLowerInvariant();
        document["label"] = title;
        document["place"] = "pagination japan";
        document["preview"] = new CatalogSearchProjection.SearchFile(
            Id("track", 1_000 + sequence),
            Id("source", 1_000 + number),
            "Set 1",
            1,
            cut,
            120,
            0,
            false,
            false,
            title,
            $"pagination-{cut}"
        );
        document["preview_track_id"] = Id("track", 1_000 + sequence);
        document["display"] = new
        {
            id = Id("track", 1_000 + sequence),
            source_id = Id("source", 1_000 + number),
            title,
            slug = $"pagination-{cut}",
        };
        return document;
    }

    private static Dictionary<string, object> PaginationOutsideJapan()
    {
        var document = PagingDocument(Id("show", 19950), "show", 18950);
        document["country"] = "US";
        document["place"] = "japan";
        return document;
    }

    private static Dictionary<string, object> ScarletParentShow(int number)
    {
        var document = PagingDocument(Id("show", 3_000 + number), "show", 2_000 + number);
        document["name"] = "scarlet fixture venue";
        document["label"] = "Scarlet Fixture Venue";
        return document;
    }

    private static Dictionary<string, object> ScarletPerformance(int number, bool played)
    {
        var document = PagingPerformance(2_000 + number, 1);
        document["id"] = played ? PlayedScarlet : $"perf_scarlet_{number}";
        document["name"] = "scarlet begonias";
        document["label"] = "Scarlet Begonias";
        document["song_id"] = "song_00000002";
        document["track_plays"] = played ? 800 : 5;
        document["ranking_signal"] = played ? 800 : 5;
        document["popularity_percentile"] = played ? .95 : .25;
        document["display_popularity"] = new
        {
            windows = new Dictionary<string, object> { ["1y"] = new { plays = played ? 800 : 5 } },
        };
        // Text retrieval ranks by the combined popularity of files and plays.
        // Leave the most-played version behind a full page of other exact hits.
        document["popularity"] = played ? 1 : 10;
        document["soundcheck"] = false;
        return document;
    }

    private static Dictionary<string, object> PhiladelphiaRecording(int id)
    {
        var document = MatchingRecording(id, 4, id, "Philly Archive");
        document["venue_id"] = "venue_00000001";
        return document;
    }

    private static Dictionary<string, object> PassageTrack(System.Text.Json.JsonElement track) =>
        new()
        {
            ["id"] = Id("track", 44),
            ["kind"] = "passage_track",
            ["artist_slug"] = "v4-review-4",
            ["display"] = track,
        };

    private static Dictionary<string, object> PartialDisplayProbe() =>
        new()
        {
            ["id"] = "missing_display_probe",
            ["kind"] = "show",
            ["artist_slug"] = "v4-review-4",
            ["name"] = "renderable probe",
        };

    private static Dictionary<string, object> RenderableProbe()
    {
        var document = RecordingParentShow(18804, 100);
        document["name"] = "renderable probe";
        return document;
    }

    private async Task StartSearchApi()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Configuration["Search:AliasPrefix"] = _prefix;
        builder.Configuration["Search:Typesense:Url"] = TypesenseUrl;
        builder.Configuration["Search:Typesense:ApiKey"] = TypesenseKey;
        builder.Configuration["Search:PlaysExport"] = null;
        builder.Configuration[CatalogSearchExchange.KeySetting] = CatalogServiceKey;
        builder.Services.AddSingleton(
            new DbService(
                LocalTestDatabase.DatabaseUrl,
                null,
                null,
                new HostingEnvironment { EnvironmentName = Environments.Production },
                NullLogger<DbService>.Instance
            )
        );
        builder.Services.AddSingleton<IBackgroundJobClient>(new RecordingPopularityJobs());
        builder.Services.AddV4Catalog();
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(CatalogSearchController).Assembly);

        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        var address = _app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        _http = new HttpClient { BaseAddress = new Uri(address) };

        var evidence = _app.Services.GetRequiredService<CatalogQueryEvidenceStore>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (evidence.Current.Collection is null)
        {
            await Task.Delay(25, deadline.Token);
        }
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        try
        {
            _http?.Dispose();
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }

            if (_seededReview)
            {
                await using var connection = await MigratedTestDatabase.OpenAsync();
                await connection.ExecuteAsync(
                    "DELETE FROM source_reviews WHERE uuid = @uuid",
                    new { uuid = _review }
                );
            }

            if (_typesense is not null)
            {
                foreach (var kind in SearchIndexSchema.Collections)
                {
                    await _typesense.DeleteAlias(
                        SearchIndexBuilder.AliasName(_prefix, kind),
                        CancellationToken.None
                    );
                }

                var collections = await _typesense.ReadCollectionNames(CancellationToken.None);
                foreach (
                    var collection in collections.Where(name =>
                        name.StartsWith(_prefix + "_", StringComparison.Ordinal)
                    )
                )
                {
                    await _typesense.DeleteCollection(collection, CancellationToken.None);
                }
            }
        }
        finally
        {
            _typesenseHttp?.Dispose();
            DbService.ConnStr = _previousPrimary;
            DbService.ReadOnlyConnStr = _previousReplica;
        }
    }

    [Test]
    public async Task VenueScopeFindsAVenueThatFallsOutsideTheUnscopedCandidatePage()
    {
        const string query = "?q=Current&country=US";
        const string venue = "venue_00000001";
        var all = await GetSearch(query);
        Results(all).Should().NotBeEmpty();
        ResourceIds(Results(all)).Should().NotContain(venue);

        var scoped = await GetSearch(query + "&scope=venues&request_id=venue-7");
        scoped["q"]!.GetValue<string>().Should().Be("Current");
        scoped["request_id"]!.GetValue<string>().Should().Be("venue-7");
        Results(scoped).Should().OnlyContain(row => (string?)row!["kind"] == "venue");
        ResourceIds(Results(scoped)).Should().Contain(venue);
    }

    [Test]
    public async Task AllOpensOneShowAtAMatchingRecordingWhileSourcesKeepsEveryVersion()
    {
        const string query = "?q=overflow%20recording%20probe";
        var all = await GetSearch(query);
        var shows = Results(all).Where(row => (string?)row!["kind"] == "show").ToArray();
        var sourceIds = Enumerable.Range(1, 5).Select(number => Id("source", 18700 + number));
        shows
            .Select(row => (string?)row!["show"]!["id"])
            .Should()
            .Equal(Id("show", 18700), Id("show", 18710));
        sourceIds.Should().Contain((string?)shows[0]!["source"]!["id"]);
        ((string?)shows[1]!["source"]!["id"]).Should().Be(Id("source", 18711));
        ((long?)shows[0]!["popularity"]!["windows"]!["1y"]!["plays"]).Should().Be(41061);
        ((long?)shows[1]!["popularity"]!["windows"]!["1y"]!["plays"]).Should().Be(75);

        var tab = await GetSearch(query + "&scope=sources");
        var sources = Results(tab);
        sources
            .Select(row => (string?)row!["source"]!["id"])
            .Should()
            .BeEquivalentTo(sourceIds.Append(Id("source", 18711)));
        ((int?)tab["counts"]?["sources"]).Should().Be(sources.Count);
        if ((int?)all["counts"]?["sources"] is { } exact)
        {
            exact.Should().Be(sources.Count);
        }
        else
        {
            ((int?)all["count_lower_bounds"]?["sources"]).Should().BeInRange(1, sources.Count);
        }
    }

    [Test]
    public async Task PublicSearchReturnsTheRecordingAndItsShowAsV4Resources()
    {
        var result = await GetSearch("?q=synthetic-review-4&scope=sources");
        var recording = Results(result)
            .Single(row => (string?)row!["source"]!["id"] == Id("source", 24));

        var show = recording!["show"]!;
        var source = recording["source"]!;
        ((string?)show["imported_venue"]!["location"]).Should().Be("Philadelphia, PA");
        ((string?)source["taper"]).Should().Be("Fixture taper");
        ((string?)source["transferrer"]).Should().Be("Fixture transferrer");
        ((float?)source["avg_rating"]).Should().Be(8.125f);
        ((int?)source["num_reviews"]).Should().Be(3);
    }

    [Test]
    public async Task VenueSearchReturnsTheCanonicalLocation()
    {
        const string query = "?q=Current%20Arena&scope=venues";
        var result = await GetSearch(query);
        var venue = Results(result)[0]!["venue"]!;
        ((string?)venue["id"]).Should().Be("venue_00000001");
        ((string?)venue["city"]).Should().Be("Philadelphia");
        ((string?)venue["region"]).Should().Be("PA");
        ((string?)venue["country"]).Should().Be("US");
    }

    [Test]
    public async Task SearchTreatsAnEmptyBoostAsNoBoost()
    {
        var control = await GetSearch(ScarletSongsQuery);

        var result = await GetSearch(ScarletSongsQuery + "&boost_artist_ids=");

        ResourceIds(Results(control)).Should().NotBeEmpty();
        ResourceIds(Results(result)).Should().Equal(ResourceIds(Results(control)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AllSearchFindsTheMostPlayedExactVersionOutsideTheTextPage(bool typing)
    {
        var result = await GetSearch(
            $"?q=Scarlet%20Begonias&typing={typing.ToString().ToLowerInvariant()}"
        );
        Results(result)
            .Select(row => (long?)row?["popularity"]?["windows"]?["1y"]?["plays"])
            .Should()
            .Contain(800);
    }

    [TestCase(13, "song_00000002")]
    [TestCase(14, "song_00000003")]
    public async Task SearchAppliesValidBoostsInAMixedList(int artistSuffix, string song)
    {
        var artist = Id("artist", artistSuffix);

        var result = await GetSearch(
            ScarletSongsQuery + $"&boost_artist_ids=&boost_artist_ids=,%20,{artist},"
        );

        ResourceIds(Results(result)).First().Should().Be(song);
    }

    [TestCase("explore")]
    [TestCase("add_tracks")]
    public async Task CountryFilterCannotBeReplacedByAConflictingCountryInTheQuery(string mode)
    {
        var query = JapanShowsQuery + $"&mode={mode}";
        var matching = await GetSearch(query + "&country=JP");
        Results(matching).Should().NotBeEmpty();

        var conflicting = await GetSearch(query + "&country=US");
        Results(conflicting).Should().BeEmpty();
    }

    [Test]
    public async Task ScopedAddTracksPagesShowsAndMatchingTitlesWithoutPlaceOnlyPerformances()
    {
        var rows = new List<JsonNode?>();
        int? page = 1;
        int? count = null;
        while (page.HasValue)
        {
            var result = await GetSearch(JapanShowsQuery + $"&mode=add_tracks&page={page.Value}");
            var results = Results(result);
            var total = (int)result["counts"]!["shows"]!;
            count ??= total;
            total.Should().Be(count.Value);
            page = (int?)result["next_page"];
            if (page.HasValue)
            {
                results.Should().HaveCount(ScopedShowPage.Size);
            }
            rows.AddRange(results);
        }

        rows.Select(row => (string?)row!["key"]).Should().OnlyHaveUniqueItems();
        rows.Should().HaveCount(count!.Value);
        rows.Where(row => (string?)row!["kind"] == "performance")
            .Should()
            .OnlyContain(row => (string?)row!["preview_track"]!["title"] == "Pagination");
        var shows = rows.GroupBy(row => row!["show"]!["id"]!.GetValue<string>()).ToArray();
        shows.Should().HaveCount(PagingShowCount);
        foreach (var show in shows)
        {
            show.Select(row => row!["kind"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo("show", "performance", "performance");
        }
    }

    [Test]
    public async Task ScopedTypeaheadRecoveryKeepsTheShowsPageBounded()
    {
        var result = await GetSearch("?q=Pagination%20z&scope=shows&typing=true");
        var rows = Results(result);
        rows.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(ScopedShowPage.Size);
        result["next_page"].Should().BeNull();
    }

    [Test]
    public async Task AddTracksReturnsTheExactPlayablePreviewAsAV4Track()
    {
        var result = await GetSearch(
            $"?q=Scarlet%20Begonias%201995-07-01&mode=add_tracks&artist_id={Id("artist", 301)}"
        );
        var row = Results(result)[0]!;
        var show = Id("show", 311);
        const string song = "song_00000101";
        ((string?)row["kind"]).Should().Be("performance");
        ((string?)row["key"]).Should().Be($"performance:{show}:{song}");
        ((string?)row["show"]!["id"]).Should().Be(show);
        ((string?)row["performance"]!["show_id"]).Should().Be(show);
        ((string?)row["performance"]!["song_id"]).Should().Be(song);

        var preview = row["preview_track"]!;
        var selected = preview["id"]!.GetValue<string>();
        ((bool?)preview["playable"]).Should().BeTrue();
        ((string?)preview["show_id"]).Should().Be(show);
        new[] { (Id("source", 321), Id("track", 341)), (Id("source", 322), Id("track", 342)) }
            .Should()
            .Contain((preview["source_id"]!.GetValue<string>(), selected));

        var v4Track = await _http.GetStringAsync("/api/v4/source-tracks/" + selected);
        JsonNode.DeepEquals(preview, JsonNode.Parse(v4Track)).Should().BeTrue();
        var performances = JsonNode.Parse(
            await _http.GetStringAsync(
                $"/api/v4/artists/{Id("artist", 301)}/performances?song_id={song}"
            )
        )!["items"]!.AsArray();
        var v4 = performances.Single(item => (string?)item!["show_id"] == show)!;
        foreach (var (field, value) in row["performance"]!.AsObject())
        {
            JsonNode.DeepEquals(value, v4[field]).Should().BeTrue(field);
        }
    }

    [Test]
    public async Task SearchKeepsTheCurrentEvidencePreviewWhenThePerformanceAliasMoves()
    {
        var query =
            $"?q=Scarlet%20Begonias%201995-07-01&mode=add_tracks&artist_id={Id("artist", 301)}";
        var previous = Results(await GetSearch(query))[0]!["preview_track"];
        ((string?)previous!["id"]).Should().NotBeNullOrEmpty();

        var performanceAlias = SearchIndexBuilder.AliasName(
            _prefix,
            SearchIndexSchema.Performances
        );
        var original = (await _typesense!.ReadAlias(performanceAlias, CancellationToken.None))!;
        var replacement = SearchIndexBuilder.CollectionName(
            _prefix,
            SearchIndexSchema.Performances,
            Guid.NewGuid().ToString("N")
        );
        await _typesense.CreateCollection(
            SearchIndexSchema.Create(SearchIndexSchema.Performances, replacement),
            CancellationToken.None
        );

        // The API's original entity evidence is still in use while a sibling
        // alias changes. Both primary retrieval and display must use its build.
        try
        {
            await _typesense.SetAlias(performanceAlias, replacement, CancellationToken.None);
            var current = Results(await GetSearch(query))[0]!["preview_track"];
            JsonNode.DeepEquals(previous, current).Should().BeTrue();
        }
        finally
        {
            await _typesense.SetAlias(performanceAlias, original, CancellationToken.None);
            await _typesense.DeleteCollection(replacement, CancellationToken.None);
        }
    }

    [TestCase("all")]
    [TestCase("reviews")]
    public async Task ReviewsRequireTheirOwnTextAndKeepTitleAndBodyMatches(string scope)
    {
        var metadata = Results(await GetSearch($"?q=orchard%20feedback&scope={scope}"));
        metadata.Should().NotContain(row => (string?)row!["kind"] == "review");
        var date = Results(await GetSearch($"?q=1998-09-22&scope={scope}"));
        date.Should().NotContain(row => (string?)row!["kind"] == "review");
        if (scope == "all")
        {
            ResourceIds(metadata).Should().Contain(Id("show", 18802));
            ResourceIds(date).Should().Contain(Id("show", 18802));
        }

        var body = Results(await GetSearch($"?q=glorious&scope={scope}"));
        ResourceIds(body).Should().Contain(Id("source_review", 18802));
        var title = Results(await GetSearch($"?q=wonderful%20tape&scope={scope}"));
        var review = title.Single(row =>
            (string?)row!["review"]?["id"] == Id("source_review", 18803)
        );
        ((string?)review!["match"]!["highlights"]!["review.title"]).Should().Be("Wonderful tape");
    }

    [Test]
    public async Task ACityNicknameAndYearLeadToTheNamedVenueShows()
    {
        var rows = Results(await GetSearch("?q=current%20arena%20philly%2095"));
        var first = rows[0]!;

        ((string?)first["kind"]).Should().Be("show");
        ((string?)first["show"]!["artist_venue"]!["venue"]!["id"]).Should().Be("venue_00000001");
        ((string?)first["show"]!["date"]).Should().StartWith("1995-12-31");

        var otherYear = Results(await GetSearch("?q=current%20arena%20philly%2094"));
        otherYear.Should().NotContain(row => (string?)row!["kind"] == "show");
    }

    [Test]
    public async Task ReviewScopeWorksWhileTypingOnlyForArtistsWithReviews()
    {
        const string query = "?q=remarkable%20encore&typing=true";
        var withReviews = await GetSearch(query + $"&scope=reviews&artist_id={Id("artist", 14)}");
        ResourceIds(Results(withReviews))
            .Should()
            .Equal(UuidTypeId.Format("source_review", _review));

        var otherArtist = await GetSearch(query + $"&artist_id={Id("artist", 11)}");
        otherArtist["counts"]!.AsObject().ContainsKey("reviews").Should().BeFalse();
        Results(otherArtist).Should().NotContain(row => (string?)row!["kind"] == "review");
        var hiddenScope = await GetSearch(query + $"&scope=reviews&artist_id={Id("artist", 11)}");
        Results(hiddenScope).AsArray().Should().BeEmpty();
    }

    private async Task<JsonNode> GetSearch(string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SearchPath + query);
        var result = await ReadSearch(request);
        result["availability"]!["catalog"]!
            .GetValue<string>()
            .Should()
            .Be("full", result.ToJsonString());
        return result;
    }

    private async Task<JsonNode> GetCandidates(string query)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CatalogSearchExchange.Path + query
        );
        request.Headers.Add(CatalogSearchExchange.Header, CatalogServiceKey);
        // Synthetic shortlist documents exercise retrieval before resource attachment.
        return await ReadSearch(request);
    }

    private async Task<JsonNode> ReadSearch(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!;
    }

    private static JsonArray Results(JsonNode reply) => reply["results"]!.AsArray();

    private static IEnumerable<string> ResourceIds(JsonArray results) =>
        results.Select(row =>
        {
            var kind = row!["kind"]!.GetValue<string>();
            return kind switch
            {
                "recording" => row["source"]!["id"]!.GetValue<string>(),
                "performance" => row["preview_track"]!["id"]!.GetValue<string>(),
                "sequence" => row["key"]!.GetValue<string>(),
                _ => row[kind]!["id"]!.GetValue<string>(),
            };
        });

    private static string Id(string kind, int suffix) =>
        UuidTypeId.Format(kind, Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"));
}
