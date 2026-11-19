using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using Relisten;
using Relisten.Accounts.Contracts.History;
using Relisten.Data;
using Relisten.Identifiers;
using RelistenUserService;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture, NonParallelizable]
public sealed class TestPlayHistoryApi
{
    private const string UploadPath = "/v1/history/plays:batch";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly PostgresIntegrationDatabase _database = new();
    private DirectoryInfo _directory = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private UserId _user;

    [OneTimeSetUp]
    public async Task Start()
    {
        await _database.StartAsync();
        await using var connection = NewConnection();
        await connection.ExecuteAsync(
            await File.ReadAllTextAsync(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures/catalog-schema.sql"
                )
            )
        );

        _directory = Directory.CreateTempSubdirectory("relisten-cloud-play-history-");
        _app = await StartAppAsync();
        _http = CreateClient(_app);
    }

    [SetUp]
    public async Task CreateAccount() =>
        _user = await _database.CreateUserAsync($"listener_{Guid.NewGuid():N}"[..30]);

    [OneTimeTearDown]
    public async Task Stop()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        if (_directory is not null)
        {
            _directory.Delete(recursive: true);
        }
        await _database.StopAsync();
    }

    [Test]
    public async Task Upload_replays_in_order_and_counts_only_new_live_plays()
    {
        var live = NewPlay();
        var imported = NewPlay() with { Origin = "anonymous_import" };
        var malformed = NewPlay() with { TrackId = "invalid-track" };
        var expired = NewPlay(DateTimeOffset.UtcNow.AddMonths(-25));
        var mistypedField = new { play_id = PlayId.New(), started_at = false };
        var upload = new
        {
            contract_version = 1,
            plays = new object[] { live, mistypedField, malformed, imported, live, expired },
        };
        var duplicateKey = JsonSerializer
            .Serialize(NewPlay(), Json)
            .Replace("\"app_type\":1", "\"app_type\":1,\"app_type\":2", StringComparison.Ordinal);
        var invalidId = """{"play_id":"\uD800"}""";
        var body = JsonSerializer.Serialize(upload, Json)[..^2] + $",{duplicateKey},{invalidId}]}}";

        var first = await UploadAsync(body);
        first
            .Results.Select(result => result.Status)
            .Should()
            .Equal(
                "accepted",
                "rejected",
                "rejected",
                "accepted",
                "duplicate",
                "rejected",
                "rejected",
                "rejected"
            );
        first.Results[0].PlayId.Should().Be(live.PlayId);
        var replay = await UploadAsync(body);
        replay
            .Results.Select(result => result.Status)
            .Should()
            .Equal(
                "duplicate",
                "rejected",
                "rejected",
                "duplicate",
                "duplicate",
                "rejected",
                "rejected",
                "rejected"
            );

        await using var connection = NewConnection();
        var rows = await connection.QueryAsync<(string PlayId, short Origin)>(
            "SELECT play_id, origin FROM user_data.plays WHERE user_id = @user",
            new { user = _user.Value }
        );
        rows.Should()
            .BeEquivalentTo(
                new[] { (live.PlayId.Value, (short)1), (imported.PlayId.Value, (short)2) }
            );
        var stored = await connection.QuerySingleAsync<StoredPlay>(
            """
            SELECT qualified_at "QualifiedAt", received_at "ReceivedAt", playlist_id "PlaylistId",
                   playlist_entry_id "PlaylistEntryId", offline "Offline"
            FROM user_data.plays WHERE user_id = @user AND play_id = @id
            """,
            new { user = _user.Value, id = live.PlayId.Value }
        );
        stored.QualifiedAt.Should().Be(live.QualifiedAt.UtcDateTime);
        stored.PlaylistId.Should().Be(live.PlaylistId!.Value.Value);
        stored.PlaylistEntryId.Should().Be(live.PlaylistEntryId!.Value.Value);
        stored.Offline.Should().BeTrue();
        var anonymous = await AnonymousPlaysAsync(connection, live);
        anonymous
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new AnonymousPlay(stored.ReceivedAt, null, live.AppType));
        (await AnonymousPlaysAsync(connection, imported)).Should().BeEmpty();
    }

    [Test]
    public async Task Compressed_chunk_accepts_late_plays_and_deduplicates_a_concurrent_retry()
    {
        var seed = NewPlay();
        await UploadAsync(new PlayBatchUpload(1, [seed]));
        await using var connection = NewConnection();
        var chunk = await connection.QuerySingleAsync<string>(
            "SELECT tableoid::regclass::text FROM user_data.plays WHERE play_id = @id",
            new { id = seed.PlayId.Value }
        );
        await connection.ExecuteAsync(
            "CALL public.convert_to_columnstore(CAST(@chunk AS regclass))",
            new { chunk }
        );
        (
            await connection.QuerySingleAsync<bool>(
                """
                SELECT is_compressed FROM timescaledb_information.chunks
                WHERE format('%I.%I', chunk_schema, chunk_name) = @chunk
                """,
                new { chunk }
            )
        ).Should().BeTrue();

        var late = NewPlay(seed.QualifiedAt);
        var upload = new PlayBatchUpload(1, [seed, late]);
        var results = await Task.WhenAll(UploadAsync(upload), UploadAsync(upload));
        results
            .Select(response => response.Results[0].Status)
            .Should()
            .OnlyContain(status => status == "duplicate");
        results
            .Select(response => response.Results[1].Status)
            .Should()
            .BeEquivalentTo("accepted", "duplicate");
        (await AnonymousPlaysAsync(connection, seed)).Should().ContainSingle();
        (await AnonymousPlaysAsync(connection, late)).Should().ContainSingle();
        (
            await connection.QuerySingleAsync<int>(
                "SELECT count(*) FROM user_data.plays WHERE user_id = @user",
                new { user = _user.Value }
            )
        )
            .Should()
            .Be(2);
    }

    [Test]
    public async Task Anonymous_write_failure_rolls_back_the_account_play_and_can_be_retried()
    {
        var play = NewPlay();
        var upload = new PlayBatchUpload(1, [play]);
        await using var connection = NewConnection();
        await connection.ExecuteAsync(
            "ALTER TABLE public.source_track_plays ADD CONSTRAINT reject_test_app CHECK (app_type <> 1) NOT VALID"
        );
        try
        {
            using var response = await PostAsync(_http, upload);
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (
                await connection.QuerySingleAsync<int>(
                    "SELECT count(*) FROM user_data.plays WHERE user_id = @user",
                    new { user = _user.Value }
                )
            )
                .Should()
                .Be(0);
            (await AnonymousPlaysAsync(connection, play)).Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "ALTER TABLE public.source_track_plays DROP CONSTRAINT reject_test_app"
            );
        }

        (await UploadAsync(upload)).Results.Single().Status.Should().Be("accepted");
        (await AnonymousPlaysAsync(connection, play)).Should().ContainSingle();
    }

    [Test]
    public async Task Upload_requires_library_write_access()
    {
        var play = NewPlay();
        var upload = new PlayBatchUpload(1, [play]);
        using var unauthenticated = await _http.PostAsJsonAsync(UploadPath, upload, Json);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var readOnly = await PostAsync(_http, upload, readOnly: true);
        readOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await UploadAsync(upload)).Results.Single().Status.Should().Be("accepted");
    }

    [Test]
    public async Task Malformed_envelopes_fail_without_partially_accepting_plays()
    {
        var play = NewPlay();
        var valid = JsonSerializer.Serialize(new PlayBatchUpload(1, [play]), Json);
        string[] malformed =
        [
            valid[..^1],
            valid.Replace(
                "\"contract_version\":1",
                "\"contract_version\":1,\"contract_version\":1"
            ),
            valid.Replace("\"contract_version\":1", "\"contract_version\":2"),
            JsonSerializer.Serialize(
                new PlayBatchUpload(1, Enumerable.Repeat(play, 501).ToArray()),
                Json
            ),
            """{"contract_version":1,"plays":null}""",
            """{"contract_versio\uD800":1,"plays":[]}""",
        ];
        foreach (var raw in malformed)
        {
            using var response = await PostAsync(_http, raw);
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.UnprocessableEntity, raw[..Math.Min(raw.Length, 100)]);
        }
        (await UploadAsync(new PlayBatchUpload(1, [play])))
            .Results.Single()
            .Status.Should()
            .Be("accepted");
    }

    [Test]
    public async Task An_unknown_track_stays_in_history_and_raw_telemetry_but_not_the_catalog_reads()
    {
        var known = NewPlay();
        var unknown = NewPlay(known.QualifiedAt);
        await using var connection = NewConnection();
        await SeedCatalogTrackAsync(connection, TrackId.Parse(known.TrackId).Value);
        var response = await UploadAsync(new PlayBatchUpload(1, [known, unknown]));
        response.Results.Select(result => result.Status).Should().Equal("accepted", "accepted");
        (await AnonymousPlaysAsync(connection, unknown)).Should().ContainSingle();

        var receivedAt = (await AnonymousPlaysAsync(connection, known)).Single().CreatedAt;
        var day = receivedAt.Date;
        var start = new DateTimeOffset(day, TimeSpan.Zero);
        var window = new { start, end = start.AddDays(1) };
        await connection.ExecuteAsync(
            "CALL refresh_continuous_aggregate('public.source_track_plays_hourly', @start, @end)",
            window
        );
        await connection.ExecuteAsync(
            "CALL refresh_continuous_aggregate('public.source_track_plays_daily', @start, @end)",
            window
        );
        var catalogTrack = TrackId.Parse(known.TrackId).Value;
        (
            await connection.QueryAsync<Guid>(
                "SELECT source_track_uuid FROM source_track_plays_hourly WHERE play_hour >= @start",
                new { start }
            )
        )
            .Should()
            .Equal(catalogTrack);
        (
            await connection.QueryAsync<Guid>(
                "SELECT source_track_uuid FROM source_track_plays_daily WHERE play_day = @start",
                new { start }
            )
        )
            .Should()
            .Equal(catalogTrack);

        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        try
        {
            var catalog = new DbService(
                LocalDatabaseUrl(),
                null,
                null,
                new HostingEnvironment { EnvironmentName = Environments.Production },
                NullLogger<DbService>.Instance
            );
            var live = await new SourceTrackPlaysService(catalog).PlayedTracksSince(
                publicActivityOnly: true
            );
            live.Select(play => play.source_track_uuid).Should().Equal(catalogTrack);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
        }
    }

    private async Task<WebApplication> StartAppAsync()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = _directory.FullName,
                ApplicationName = typeof(AccountsApplication).Assembly.GetName().Name,
            }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ACCOUNTS_DATABASE_URL"] = _database.ConnectionString,
                ["ACCOUNTS_LOCK_DATABASE_URL"] = _database.ConnectionString,
                ["Accounts:Issuer"] = "https://auth.relisten.net/",
                ["Accounts:WebClientSecret"] = "disposable-history-test-secret",
            }
        );
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ProofAuthentication>("proof", _ => { });
        builder.Services.PostConfigure<PolicySchemeOptions>(
            AuthenticationConstants.AccountCredentialScheme,
            options => options.ForwardDefaultSelector = _ => "proof"
        );
        var app = AccountsApplication.Build(builder);
        await AccountsApplication.InitializeAsync(app, CancellationToken.None);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://accounts.relisten.net");
        return client;
    }

    private async Task<PlayBatchUploaded> UploadAsync(object body)
    {
        using var response = await PostAsync(_http, body);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlayBatchUploaded>(Json))!;
    }

    private async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        object body,
        bool readOnly = false
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UploadPath)
        {
            Content = body is string raw
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : JsonContent.Create(body, options: Json),
        };
        request.Headers.Add("X-Proof-Account", _user.Value);
        if (readOnly)
        {
            request.Headers.Add("X-Proof-Read-Only", "true");
        }
        return await client.SendAsync(request);
    }

    private NpgsqlConnection NewConnection() => new(_database.ConnectionString);

    private static PlayUpload NewPlay(DateTimeOffset? qualifiedAt = null)
    {
        // Round to seconds so the input compares exactly against PostgreSQL timestamptz.
        var qualified =
            qualifiedAt
            ?? DateTimeOffset.FromUnixTimeSeconds(
                DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds()
            );
        return new(
            PlayId.New(),
            new TrackId(Guid.NewGuid()).ToString(),
            qualified,
            qualified.AddMinutes(-4),
            AppType: 1,
            Offline: true,
            Origin: "live",
            PlaylistId.New(),
            PlaylistEntryId.New(),
            420_000
        );
    }

    private static Task<IEnumerable<AnonymousPlay>> AnonymousPlaysAsync(
        NpgsqlConnection connection,
        PlayUpload play
    ) =>
        connection.QueryAsync<AnonymousPlay>(
            """
            SELECT created_at "CreatedAt", user_uuid "UserUuid", app_type "AppType"
            FROM public.source_track_plays WHERE source_track_uuid = @track
            """,
            new { track = TrackId.Parse(play.TrackId).Value }
        );

    private static Task SeedCatalogTrackAsync(NpgsqlConnection connection, Guid track) =>
        connection.ExecuteAsync(
            """
            INSERT INTO artists (id, uuid, name, slug, updated_at)
                VALUES (1, gen_random_uuid(), 'History artist', 'history-artist', now());
            INSERT INTO shows (id, uuid, artist_id, date, display_date, updated_at)
                VALUES (1, gen_random_uuid(), 1, '2020-01-01', '2020-01-01', now());
            INSERT INTO sources (id, uuid, artist_id, show_id, is_soundboard, is_remaster,
                                 upstream_identifier, has_jamcharts, display_date, updated_at)
                VALUES (1, gen_random_uuid(), 1, 1, false, false, 'history-source', false, '2020-01-01', now());
            INSERT INTO source_sets (id, source_id, uuid, name, updated_at)
                VALUES (1, 1, gen_random_uuid(), 'Set 1', now());
            INSERT INTO source_tracks (id, uuid, artist_id, source_id, source_set_id, track_position,
                                       title, slug, mp3_url, duration, updated_at)
                VALUES (1, @track, 1, 1, 1, 1, 'Track', 'track', 'https://example.test/track.mp3', 420, now());
            """,
            new { track }
        );

    private string LocalDatabaseUrl()
    {
        var connection = new NpgsqlConnectionStringBuilder(_database.ConnectionString);
        return new UriBuilder("postgres", connection.Host, connection.Port, connection.Database)
        {
            UserName = connection.Username,
            Password = connection.Password,
        }
            .Uri
            .AbsoluteUri;
    }

    private sealed record StoredPlay(
        DateTime QualifiedAt,
        DateTime ReceivedAt,
        string? PlaylistId,
        string? PlaylistEntryId,
        bool Offline
    );

    private sealed record AnonymousPlay(DateTime CreatedAt, Guid? UserUuid, short AppType);

    private sealed class ProofAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccountsDbContext database,
        CurrentAccountContext account
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!UserId.TryParse(Request.Headers["X-Proof-Account"], out var id))
            {
                return AuthenticateResult.NoResult();
            }
            var user = await database.Users.SingleAsync(row => row.Id == id);
            var capabilities =
                Request.Headers["X-Proof-Read-Only"] == "true"
                    ? IdentitySessionCapabilities.LibraryRead
                    : IdentitySessionCapabilities.AllWeb;
            account.SetWeb(user, SessionId.New(), "https://relisten.net", capabilities);
            return AuthenticateResult.Success(
                new(
                    new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, id.Value)],
                            "proof"
                        )
                    ),
                    "proof"
                )
            );
        }
    }
}
