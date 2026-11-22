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
using Relisten.Controllers.V4;
using Relisten.Identifiers;
using Relisten.Search;
using RelistenApiTests.Popularity;
using RelistenApiTests.V4;

namespace RelistenApiTests.Search;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestCatalogSearchHttp
{
    private const string SearchPath = "/api/v4/search";
    private const string TypesenseUrl = "http://127.0.0.1:18108";
    private const string TypesenseKey = "local_dev_typesense";
    private const int PagingShowCount = 20;
    private const string JapanShowsQuery = "?q=Pagination%20Japan&scope=shows";
    private const string ScarletSongsQuery = "?q=Scarlet%20Begonias&scope=songs&typing=true";
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
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, transaction),
            [],
            CancellationToken.None
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
            });
        // Each upstream page can include both shows and performance groups.
        // The first combined page is large enough to detect truncation after paging.
        var showNumbers = Enumerable.Range(1, PagingShowCount);
        var pageShows = showNumbers.Select(number =>
            PagingDocument(Id("show", 1_000 + number), "show", number)
        );
        var pagePerformances = showNumbers.SelectMany(number =>
            Enumerable.Range(1, 2).Select(cut => PagingPerformance(number, cut))
        );
        var documents = projection.Documents(CancellationToken.None);
        await index.Build(
            _prefix,
            Guid.NewGuid().ToString("N"),
            documents with
            {
                Entities = documents.Entities.Concat(
                    distractors.Concat(pageShows).ToAsyncEnumerable()
                ),
                Performances = documents.Performances.Concat(pagePerformances.ToAsyncEnumerable()),
            },
            CancellationToken.None
        );
    }

    private static Dictionary<string, object> PagingDocument(string id, string kind, int number) =>
        new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = "pagination",
            ["label"] = "Pagination",
            ["country"] = "JP",
            ["show_id"] = Id("show", 1_000 + number),
            ["popularity"] = number,
        };

    private static Dictionary<string, object> PagingPerformance(int number, int cut)
    {
        var sequence = number * 2 + cut;
        var document = PagingDocument($"perf_{sequence:x24}", "performance", number);
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
            "Pagination"
        );
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
        all["top"]!.AsArray().Should().NotBeEmpty();
        Ids(all["sections"]!["venues"]!).Should().NotContain(venue);

        var scoped = await GetSearch(query + "&scope=venues&request_id=venue-7");
        scoped["q"]!.GetValue<string>().Should().Be("Current");
        scoped["request_id"]!.GetValue<string>().Should().Be("venue-7");
        scoped["page"]!.GetValue<int>().Should().Be(1);
        scoped["results"]!.AsArray().Should().OnlyContain(row => (string?)row!["kind"] == "venue");
        Ids(scoped["results"]!).Should().Contain(venue);
    }

    [Test]
    public async Task SearchTreatsAnEmptyBoostAsNoBoost()
    {
        var control = await GetSearch(ScarletSongsQuery);

        var result = await GetSearch(ScarletSongsQuery + "&boost_artist_ids=");

        Ids(control["results"]!).Should().NotBeEmpty();
        Ids(result["results"]!).Should().Equal(Ids(control["results"]!));
    }

    [TestCase(13, "song_00000002")]
    [TestCase(14, "song_00000003")]
    public async Task SearchAppliesValidBoostsInAMixedList(int artistSuffix, string song)
    {
        var artist = Id("artist", artistSuffix);

        var result = await GetSearch(
            ScarletSongsQuery + $"&boost_artist_ids=&boost_artist_ids=,%20,{artist},"
        );

        Ids(result["results"]!).First().Should().Be(song);
    }

    [TestCase("explore")]
    [TestCase("add_tracks")]
    public async Task CountryFilterCannotBeReplacedByAConflictingCountryInTheQuery(string mode)
    {
        var query = JapanShowsQuery + $"&mode={mode}";
        var matching = await GetSearch(query + "&country=JP");
        matching["results"]!.AsArray().Should().NotBeEmpty();

        var conflicting = await GetSearch(query + "&country=US");
        conflicting["interpretation"]!["country"]!.GetValue<string>().Should().Be("US");
        conflicting["results"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task ScopedAddTracksPagesEveryShowAndBothMatchingPerformances()
    {
        var first = await GetSearch(JapanShowsQuery + "&mode=add_tracks");
        var second = await GetSearch(JapanShowsQuery + "&mode=add_tracks&page=2");
        second["page"]!.GetValue<int>().Should().Be(2);
        second["results"]!.AsArray().Should().NotBeEmpty();

        var firstResults = first["results"]!;
        var secondResults = second["results"]!;
        Ids(firstResults).Concat(Ids(secondResults)).Should().OnlyHaveUniqueItems();
        var shows = firstResults
            .AsArray()
            .Concat(secondResults.AsArray())
            .GroupBy(row => row!["show_id"]!.GetValue<string>())
            .ToArray();
        shows.Should().HaveCount(PagingShowCount);
        foreach (var show in shows)
        {
            show.Select(row => row!["kind"]!.GetValue<string>())
                .Should()
                .BeEquivalentTo("show", "performance", "performance");
        }
    }

    [Test]
    public async Task AddTracksPerformancePreviewIsOneOfTheExactFilesInItsDrillIn()
    {
        var result = await GetSearch(
            $"?q=Scarlet%20Begonias%201995-07-01&mode=add_tracks&artist_id={Id("artist", 301)}"
        );
        var performance = result["top"]![0]!;
        performance["kind"]!.GetValue<string>().Should().Be("performance");
        performance["show_id"]!.GetValue<string>().Should().Be(Id("show", 311));
        var key = performance["performance_key"]!.GetValue<string>();
        var preview = performance["preview"]!;

        var drillIn = await _http.GetStringAsync(SearchPath + "/performance?key=" + key);
        var detail = JsonNode.Parse(drillIn)!;
        detail["performance_key"]!.GetValue<string>().Should().Be(key);
        detail["show_id"]!.GetValue<string>().Should().Be(Id("show", 311));
        var files = detail["sources"]!
            .AsArray()
            .SelectMany(source =>
                source!["files"]!
                    .AsArray()
                    .Select(file => (Source: (string?)source["source_id"], File: file!))
            )
            .ToArray();
        files
            .Select(cut => (cut.Source, (string?)cut.File["track_id"]))
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Id("source", 321), Id("track", 341)),
                    (Id("source", 322), Id("track", 342)),
                }
            );
        files.Should().ContainSingle(cut => JsonNode.DeepEquals(cut.File, preview));
    }

    [Test]
    public async Task ReviewScopeWorksWhileTypingOnlyForArtistsWithReviews()
    {
        const string query = "?q=remarkable%20encore&typing=true";
        var withReviews = await GetSearch(query + $"&scope=reviews&artist_id={Id("artist", 14)}");
        Ids(withReviews["results"]!).Should().Equal(UuidTypeId.Format("review", _review));

        var otherArtist = await GetSearch(query + $"&artist_id={Id("artist", 11)}");
        otherArtist["counts"]!.AsObject().ContainsKey("reviews").Should().BeFalse();
        otherArtist["sections"]!.AsObject().ContainsKey("reviews").Should().BeFalse();
        var hiddenScope = await GetSearch(query + $"&scope=reviews&artist_id={Id("artist", 11)}");
        hiddenScope["results"]!.AsArray().Should().BeEmpty();
    }

    private async Task<JsonNode> GetSearch(string query)
    {
        using var response = await _http.GetAsync(SearchPath + query);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var result = JsonNode.Parse(body)!;
        result["availability"]!.GetValue<string>().Should().Be("full", body);
        return result;
    }

    private static IEnumerable<string> Ids(JsonNode rows) =>
        rows.AsArray().Select(row => row!["id"]!.GetValue<string>());

    private static string Id(string kind, int suffix) =>
        UuidTypeId.Format(kind, Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"));
}
