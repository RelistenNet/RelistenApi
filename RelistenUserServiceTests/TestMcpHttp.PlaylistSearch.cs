using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Relisten;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Identifiers;
using Relisten.Search;
using RelistenIngestionWorker.PlaylistSearch;
using RelistenUserService.Playlists.Entities;
using RelistenUserService.Search;

namespace RelistenUserServiceTests;

public sealed partial class TestMcpHttp
{
    [Test]
    public async Task SeededPlaylistSearchEvalThroughHttpAndMcp()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Requires local catalog restore and Typesense.");
        }

        var alias = "test_merged_playlists_" + Guid.NewGuid().ToString("N");
        var primary = DbService.ConnStr;
        var replica = DbService.ReadOnlyConnStr;
        using var typesenseHttp = new HttpClient();
        TypesenseClient.Configure(typesenseHttp, "http://127.0.0.1:18108", "local_dev_typesense");
        var typesense = new TypesenseClient(typesenseHttp);
        try
        {
            var catalogDb = new DbService(
                "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db",
                null,
                null,
                new HostingEnvironment { EnvironmentName = Environments.Production },
                NullLogger<DbService>.Instance
            );
            var track = await GratefulDeadTrack(catalogDb);
            var catalogAvailable = true;
            await StartApp(
                playlistAlias: alias,
                catalogResponse: _ => new
                {
                    availability = catalogAvailable ? "full" : "unavailable",
                    top = new[]
                    {
                        new
                        {
                            id = track.Artist.ToString(),
                            kind = "artist",
                            label = "Grateful Dead",
                        },
                    },
                    sections = new { },
                }
            );
            var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes);
            var cookie = await SignIn(fields);
            using var authorize = await Authorize(fields, cookie);
            var native = await Exchange(Native, NativeRedirect, ReadCode(authorize));
            using var profile = await Get(AccountsOrigin + "/v1/me", native.Access);
            var owner = UserId.Parse((await Json(profile)).GetProperty("user_id").GetString()!);
            var mcp = await ConnectChatGpt("google-alice");

            var fixture = await SeedSearchPlaylists(owner, track.Track);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Search:PlaylistAlias"] = alias }
                )
                .Build();
            var index = new PlaylistSearchIndex(
                new(database.ConnectionString, catalogDb),
                typesense,
                config
            );
            await index.Rebuild(CancellationToken.None);

            await AssertPublicPlaylistSearch(fixture, track.Artist);
            catalogAvailable = false;
            var partial = await SearchHttp("Dead Mondays", "all");
            partial.GetProperty("catalog_availability").GetString().Should().Be("unavailable");
            PlaylistIds(partial).Should().Equal(fixture.Mondays.Value);
            catalogAvailable = true;
            await AssertPrivatePlaylistSearch(fixture.Private, native.Access, mcp.Access);
            await AssertStalePlaylistSearch(fixture, owner);
            await AssertPaginationSurvivesAStaleHit(index, owner);
            await AssertReplacedHistoryRejectsIndexedMatches(fixture.Mondays);
        }
        finally
        {
            DbService.ConnStr = primary;
            DbService.ReadOnlyConnStr = replica;
            await typesense.DeleteAlias(alias, CancellationToken.None);
            foreach (
                var name in (await typesense.ReadCollectionNames(CancellationToken.None)).Where(
                    name => name.StartsWith(alias + "_", StringComparison.Ordinal)
                )
            )
            {
                await typesense.DeleteCollection(name, CancellationToken.None);
            }
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task MergedSearchReturnsTheAvailableHalf(bool catalogAvailable)
    {
        await StartApp(
            catalogResponse: _ => new { availability = catalogAvailable ? "full" : "unavailable" },
            playlistSearch: new PlaylistSearchStub(
                catalogAvailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
            )
        );
        var response = await SearchHttp("a playlist", "all", requestId: "roundtrip");
        response
            .GetProperty("catalog_availability")
            .GetString()
            .Should()
            .Be(catalogAvailable ? "full" : "unavailable");
        response
            .GetProperty("playlist_availability")
            .GetString()
            .Should()
            .Be(catalogAvailable ? "unavailable" : "full");
        response.GetProperty("request_id").GetString().Should().Be("roundtrip");
    }

    [Test]
    public async Task CancellingTheRequestInterruptsPlaylistLookahead()
    {
        using var handler = new BlockingPlaylistLookahead();
        await StartApp(playlistSearch: handler);
        await using var scope = app.Services.CreateAsyncScope();
        var search = scope.ServiceProvider.GetRequiredService<MergedSearch>();
        using var cancellation = new CancellationTokenSource();

        var request = new MergedSearchRequest { Q = "a playlist", Scope = "playlists" };
        var response = search.Run(request, viewer: null, cancellation.Token);
        await handler.Lookahead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Func<Task> finish = () => response.WaitAsync(TimeSpan.FromSeconds(5));
        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class BlockingPlaylistLookahead : HttpMessageHandler
    {
        public TaskCompletionSource Lookahead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (QueryHelpers.ParseQuery(request.RequestUri!.Query)["page"] != "1")
            {
                Lookahead.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            // Fill the first Typesense page with IDs removed from the accounts database.
            var hits = Enumerable
                .Range(0, 20)
                .Select(_ => new
                {
                    document = new
                    {
                        id = PlaylistId.New().Value,
                        history_generation = "stale",
                        revision = 1,
                    },
                });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { hits }) };
        }
    }

    [TestCase("all")]
    [TestCase("artists")]
    public async Task SearchRequiresRefreshForAnInvalidBearer(string scope)
    {
        await StartApp();
        var path = AccountsOrigin + "/v1/search?q=synthetic&scope=" + scope;

        using var invalid = await Get(path, "invalid-bearer");
        invalid.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        invalid.Headers.WwwAuthenticate.Should().Contain(challenge => challenge.Scheme == "Bearer");

        using var anonymous = await Send(new(HttpMethod.Get, path));
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task SearchRequiresRefreshWhenTheBearerExpires()
    {
        var clock = new SearchValidationClock();
        await StartApp(validationClock: clock);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes);
        var cookie = await SignIn(fields);
        using var authorize = await Authorize(fields, cookie);
        var native = await Exchange(Native, NativeRedirect, ReadCode(authorize));
        var path = AccountsOrigin + "/v1/search?q=synthetic";

        using var valid = await Get(path, native.Access);
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        clock.Offset = TimeSpan.FromMinutes(20);

        using var expired = await Get(path, native.Access);
        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class SearchValidationClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    [Test]
    public async Task SearchRejectsARevokedNativeSession()
    {
        await StartApp();
        var fields = AuthorizeFields(Native, NativeRedirect, "openid offline_access user.read");
        var cookie = await SignIn(fields);
        using var authorize = await Authorize(fields, cookie);
        var native = await Exchange(Native, NativeRedirect, ReadCode(authorize));
        var path = AccountsOrigin + "/v1/search?q=synthetic&scope=artists";
        using var valid = await Get(path, native.Access);
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        using var profile = await Get(AccountsOrigin + "/v1/me", native.Access);
        var id = SessionId.Parse(
            (await Json(profile)).GetProperty("native_session_id").GetString()!
        );

        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        await db
            .NativeSessions.Where(row => row.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAt, now));

        using var revoked = await Get(path, native.Access);
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        revoked.Headers.WwwAuthenticate.Should().Contain(challenge => challenge.Scheme == "Bearer");
    }

    private async Task AssertPublicPlaylistSearch(PlaylistSearchFixture fixture, ArtistId artist)
    {
        var byName = await SearchHttp("Dead Mondays");
        PlaylistIds(byName).Should().Contain(fixture.Mondays.Value);
        byName.GetProperty("catalog_availability").GetString().Should().Be("not_requested");

        var byArtist = await SearchHttp("grateful dead", "all");
        byArtist
            .GetProperty("catalog")
            .GetProperty("top")[0]
            .GetProperty("kind")
            .GetString()
            .Should()
            .Be("artist");
        PlaylistIds(byArtist).Should().Equal(fixture.Mondays.Value, fixture.RoadTrip.Value);
        PlaylistIds(await SearchHttp("bertha", artist: artist.ToString()))
            .Should()
            .Equal(fixture.RoadTrip.Value);
        PlaylistIds(await SearchHttp("curatorbluejay"))
            .Should()
            .Contain(fixture.Mondays.Value, fixture.RoadTrip.Value);
        PlaylistIds(await SearchHttp("glowstick taxonomy")).Should().Equal(fixture.Mondays.Value);
        PlaylistIds(await SearchHttp("cassette almanac")).Should().Equal(fixture.Mondays.Value);
    }

    private async Task AssertPrivatePlaylistSearch(PlaylistId id, string native, string mcp)
    {
        const string query = "Afterhours Notebook";
        PlaylistIds(await SearchHttp(query)).Should().BeEmpty();
        PlaylistIds(await SearchHttp(query, token: native)).Should().Equal(id.Value);
        var guest = await ConnectChatGpt("google-shared");
        (await Call("find", new { text = query }, guest.Access))
            .GetProperty("structuredContent")
            .GetProperty("playlists")
            .GetArrayLength()
            .Should()
            .Be(0);
        var match = (await Call("find", new { text = query }, mcp))
            .GetProperty("structuredContent")
            .GetProperty("playlists")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Subject;
        match.GetProperty("id").GetString().Should().Be(id.Value);
        match
            .GetProperty("untrusted_listener_text")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be(query);
    }

    private async Task AssertStalePlaylistSearch(PlaylistSearchFixture fixture, UserId owner)
    {
        await using var db = database.CreateContext();
        var road = await db.Playlists.SingleAsync(row => row.Id == fixture.RoadTrip);
        var note = await db.PlaylistEntries.SingleAsync(row => row.PlaylistId == fixture.Mondays);
        var curator = await db.Users.SingleAsync(row => row.Id == owner);
        var monday = await db.Playlists.SingleAsync(row => row.Id == fixture.Mondays);

        road.Published = false;
        await db.SaveChangesAsync();
        PlaylistIds(await SearchHttp("Road Trip Reels")).Should().BeEmpty();

        road.Published = true;
        road.TakenDown = true;
        await db.SaveChangesAsync();
        PlaylistIds(await SearchHttp("Road Trip Reels")).Should().BeEmpty();

        road.TakenDown = false;
        road.Archived = true;
        await db.SaveChangesAsync();
        PlaylistIds(await SearchHttp("Road Trip Reels")).Should().BeEmpty();

        road.Archived = false;
        note.Description = "Replacement text";
        monday.Revision++;
        await db.SaveChangesAsync();
        PlaylistIds(await SearchHttp("glowstick taxonomy")).Should().BeEmpty();

        // The road-trip playlist still has its indexed revision and visibility.
        curator.Username = "curatornewhandle";
        curator.UsernameVersion++;
        await db.SaveChangesAsync();
        var oldHandle = await SearchHttp("curatorbluejay");
        var card = oldHandle
            .GetProperty("playlists")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Subject;
        card.GetProperty("id").GetString().Should().Be(fixture.RoadTrip.Value);
        card.GetProperty("curator").GetString().Should().Be("curatornewhandle");
        PlaylistIds(await SearchHttp("Road Trip Reels")).Should().Contain(fixture.RoadTrip.Value);
    }

    private async Task AssertPaginationSurvivesAStaleHit(PlaylistSearchIndex index, UserId owner)
    {
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Playlists.AddRange(
            Enumerable
                .Range(1, 21)
                .Select(number => new Playlist
                {
                    Id = PlaylistId.New(),
                    OwnerId = owner,
                    Name = $"Pagination needle {number}",
                    Published = true,
                    PublicCode = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    CreatedAt = now,
                    UpdatedAt = now.AddSeconds(number),
                })
        );
        await db.SaveChangesAsync();
        await index.Rebuild(CancellationToken.None);

        var fullPage = await SearchHttp("pagination needle");
        var ids = PlaylistIds(fullPage);
        ids.Should().HaveCount(20);
        var stale = PlaylistId.Parse(ids[0]!);
        (await db.Playlists.SingleAsync(row => row.Id == stale)).Published = false;
        await db.SaveChangesAsync();

        var shortPage = await SearchHttp("pagination needle");
        PlaylistIds(shortPage).Should().HaveCount(19).And.NotContain(stale.Value);
        var nextPage = shortPage.GetProperty("playlist_next_page").GetInt32();
        PlaylistIds(await SearchHttp("pagination needle", page: nextPage)).Should().ContainSingle();
    }

    private async Task AssertReplacedHistoryRejectsIndexedMatches(PlaylistId id)
    {
        PlaylistIds(await SearchHttp("Dead Mondays")).Should().Equal(id.Value);
        await using var db = database.CreateContext();
        var generation = Guid.CreateVersion7().ToString();
        await db.HistoryGenerations.ExecuteUpdateAsync(update =>
            update.SetProperty(row => row.Value, generation)
        );
        PlaylistIds(await SearchHttp("Dead Mondays")).Should().BeEmpty();
    }

    private async Task<JsonElement> SearchHttp(
        string query,
        string scope = "playlists",
        string? token = null,
        string? artist = null,
        string? requestId = null,
        int page = 1
    )
    {
        var path = QueryHelpers.AddQueryString(
            AccountsOrigin + "/v1/search",
            new Dictionary<string, string?>
            {
                ["q"] = query,
                ["scope"] = scope,
                ["typing"] = "true",
                ["artist_id"] = artist,
                ["request_id"] = requestId,
                ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await Send(request);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return await Json(response);
    }

    private static string?[] PlaylistIds(JsonElement response) =>
        response
            .GetProperty("playlists")
            .EnumerateArray()
            .Select(row => row.GetProperty("id").GetString())
            .ToArray();

    private static async Task<(ArtistId Artist, TrackId Track)> GratefulDeadTrack(DbService catalog)
    {
        await using var db = catalog.CreateConnection(longTimeout: false, readOnly: false);
        var item = await db.QuerySingleOrDefaultAsync<(Guid Artist, Guid Track)>(
            """
            SELECT a.uuid, t.uuid FROM artists a JOIN source_tracks t ON t.artist_id = a.id
            WHERE a.slug = 'grateful-dead' AND lower(t.title) = 'bertha' AND NOT t.is_orphaned
            ORDER BY t.id LIMIT 1
            """
        );
        if (item == default)
        {
            Assert.Ignore("The local catalog restore must contain a Grateful Dead Bertha.");
        }
        return (new ArtistId(item.Artist), new TrackId(item.Track));
    }

    private async Task<PlaylistSearchFixture> SeedSearchPlaylists(UserId owner, TrackId track)
    {
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(row => row.Id == owner);
        user.Username = "curatorbluejay";
        user.UsernameVersion++;
        var now = DateTimeOffset.UtcNow;
        Playlist Playlist(string name, bool published) =>
            new()
            {
                Id = PlaylistId.New(),
                OwnerId = owner,
                Name = name,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
                Published = published,
                PublicCode = published ? Guid.NewGuid().ToString("N") : null,
            };
        var mondays = Playlist("Grateful Dead Mondays", true);
        var road = Playlist("Road Trip Reels", true);
        var hidden = Playlist("Afterhours Notebook", false);
        db.Playlists.AddRange(mondays, road, hidden);
        var guide = new PlaylistSegment
        {
            Id = PlaylistSegmentId.New(),
            PlaylistId = mondays.Id,
            Rank = "a",
            Description = "cassette almanac",
        };
        var music = new PlaylistSegment
        {
            Id = PlaylistSegmentId.New(),
            PlaylistId = road.Id,
            Rank = "a",
        };
        db.PlaylistSegments.AddRange(guide, music);
        db.PlaylistEntries.AddRange(
            new PlaylistEntry
            {
                Id = PlaylistEntryId.New(),
                PlaylistId = mondays.Id,
                SegmentId = guide.Id,
                Rank = "a",
                Description = "glowstick taxonomy",
            },
            new PlaylistEntry
            {
                Id = PlaylistEntryId.New(),
                PlaylistId = road.Id,
                SegmentId = music.Id,
                Rank = "a",
                TrackId = track.ToString(),
            }
        );
        await db.SaveChangesAsync();
        return new(mondays.Id, road.Id, hidden.Id);
    }

    private sealed record PlaylistSearchFixture(
        PlaylistId Mondays,
        PlaylistId RoadTrip,
        PlaylistId Private
    );

    private sealed class PlaylistSearchStub(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new { hits = Array.Empty<object>() }),
                }
            );
    }
}
