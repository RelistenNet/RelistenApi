using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenIddict.EntityFrameworkCore.Models;
using Relisten.Identifiers;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using RelistenUserService.Sonos;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestSonosDevelopment
{
    private const string Track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
    private const string Host = "sonos-dev.example";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Test]
    public async Task Linked_account_loads_an_ordered_queue_and_observes_a_signed_pause()
    {
        var database = new PostgresIntegrationDatabase();
        try
        {
            await database.StartAsync();
            await ExerciseAsync(database);
        }
        finally
        {
            await database.StopAsync();
        }
    }

    private static async Task ExerciseAsync(PostgresIntegrationDatabase database)
    {
        var userId = await database.CreateUserAsync("sonos_test");
        var nativeId = SessionId.New();
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(row => row.Id == userId);
        var authorizationId = Guid.CreateVersion7();
        db.Add(new OpenIddictEntityFrameworkCoreAuthorization<Guid> { Id = authorizationId });
        db.NativeSessions.Add(
            new NativeSession
            {
                Id = nativeId,
                UserId = userId,
                AuthorizationId = authorizationId,
                SecurityVersion = user.SecurityVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow,
                AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            }
        );
        await db.SaveChangesAsync();

        var upstream = new TestUpstream();
        var clock = new TestClock();
        using var http = new HttpClient(upstream);
        using var catalog = new HttpClient(upstream, disposeHandler: false)
        {
            BaseAddress = new Uri("https://catalog.example/"),
        };
        var protocol = new SonosProtocol(http);
        var locks = new AdvisoryLockService(db);
        var link = new SonosAccountLink(
            db,
            protocol,
            new EphemeralDataProtectionProvider(),
            locks,
            TimeProvider.System
        );
        var handoff = new SonosHandoff(
            db,
            locks,
            protocol,
            new ClientFactory(catalog),
            link,
            clock,
            NullLogger<SonosHandoff>.Instance
        );
        var development = new SonosDevelopment(
            new SonosConfiguration("dev-id", "dev-secret", new Uri($"https://{Host}/"), "270")
        );
        var account = new CurrentAccountContext();
        account.SetNative(user, nativeId);
        var control = new SonosControlController(account, development, link, handoff, db, clock);
        var player = new SonosPlayerController(db, link, clock, development)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        player.Request.Host = new HostString(Host);

        var authorization = AsJson((OkObjectResult)await control.Connect(Ct))
            .GetProperty("authorization_url")
            .GetString()!;
        var state = QueryHelpers.ParseQuery(new Uri(authorization).Query)["state"].ToString();
        (await player.Callback(state, "one-time-code", Ct)).Should().BeOfType<ContentResult>();
        (await player.Callback(state, "one-time-code", Ct))
            .Should()
            .BeOfType<BadRequestObjectResult>();

        var mobileAuthorization = AsJson((OkObjectResult)await control.Connect(Ct, "mobile"))
            .GetProperty("authorization_url")
            .GetString()!;
        var mobileState = QueryHelpers
            .ParseQuery(new Uri(mobileAuthorization).Query)["state"]
            .ToString();
        var callback = (RedirectResult)await player.Callback(mobileState, "mobile-code", Ct);
        callback.Url.Should().Be("net.relisten.mobile:/sonos/callback?result=connected");
        (await player.Callback(mobileState, "mobile-code", Ct))
            .Should()
            .BeOfType<BadRequestObjectResult>();

        var incompleteAuthorization = AsJson((OkObjectResult)await control.Connect(Ct, "mobile"))
            .GetProperty("authorization_url")
            .GetString()!;
        var incompleteState = QueryHelpers
            .ParseQuery(new Uri(incompleteAuthorization).Query)["state"]
            .ToString();
        upstream.IncompleteToken = true;
        var failedCallback = (RedirectResult)
            await player.Callback(incompleteState, "bad-token", Ct);
        failedCallback.Url.Should().Be("net.relisten.mobile:/sonos/callback?result=failed");
        upstream.IncompleteToken = false;
        (await player.Callback(incompleteState, "bad-token", Ct))
            .Should()
            .BeOfType<BadRequestObjectResult>();

        var groups = AsJson((OkObjectResult)await control.Groups(Ct));
        groups[0].GetProperty("name").GetString().Should().Be("Office");
        groups[0].GetProperty("household_id").GetString().Should().Be("home");
        groups[1].GetProperty("name").GetString().Should().Be("Office");
        groups[1].GetProperty("household_id").GetString().Should().Be("studio");
        var request = new SonosHandoffRequest(
            Guid.CreateVersion7(),
            "office",
            [Track, Track],
            1,
            1234
        );
        (await control.Start(request, Ct)).Should().BeOfType<OkObjectResult>();
        (await control.Start(request, Ct)).Should().BeOfType<OkObjectResult>();
        upstream.LoadCount.Should().Be(1);
        upstream.Load.GetProperty("positionMillis").GetInt32().Should().Be(1234);
        upstream
            .Load.GetProperty("queueBaseUrl")
            .GetString()
            .Should()
            .Be($"https://{Host}/sonos/cloud-queue/{request.RequestId}/v2.3/");

        (await player.ItemWindow(request.RequestId, null, ct: Ct))
            .Should()
            .BeOfType<NotFoundResult>();
        player.Request.Headers.Authorization = upstream
            .Load.GetProperty("httpAuthorization")
            .GetString();
        var window = (JsonResult)await player.ItemWindow(request.RequestId, null, ct: Ct);
        var items = JsonSerializer
            .SerializeToElement(window.Value, (JsonSerializerOptions)window.SerializerSettings!)
            .GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("id").Should().NotBe(items[1].GetProperty("id"));
        items[1]
            .GetProperty("id")
            .GetString()
            .Should()
            .Be(upstream.Load.GetProperty("itemId").GetString());
        items[0]
            .GetProperty("track")
            .GetProperty("id")
            .GetProperty("objectId")
            .GetString()
            .Should()
            .Be($"TrackU:{Track}");

        var track = items[1].GetProperty("track");
        track.GetProperty("artist").GetProperty("name").GetString().Should().Be("Grateful Dead");
        track
            .GetProperty("album")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("1977-05-08 · Barton Hall, Cornell University");
        track
            .GetProperty("imageUrl")
            .GetString()
            .Should()
            .Be("https://relisten.net/album-art/104c96e5-719f-366f-b72d-8d53709c80e0.png");

        player.Request.Headers["X-Sonos-Event-Seq-Id"] = "9";
        player.Request.Headers["X-Sonos-Namespace"] = "playback";
        player.Request.Headers["X-Sonos-Type"] = "playbackStatus";
        player.Request.Headers["X-Sonos-Target-Type"] = "groupId";
        player.Request.Headers["X-Sonos-Target-Value"] = "office";
        player.Request.Headers["X-Sonos-Household-Id"] = "home";
        var pause = JsonSerializer.SerializeToElement(
            new
            {
                playbackState = "PLAYBACK_STATE_PAUSED",
                itemId = upstream.Load.GetProperty("itemId").GetString(),
            }
        );
        (await player.Events(pause, Ct)).Should().BeOfType<UnauthorizedResult>();
        player.Request.Headers["X-Sonos-Event-Signature"] = WebEncoders.Base64UrlEncode(
            SHA256.HashData(
                Encoding.UTF8.GetBytes("9playbackplaybackStatusgroupIdofficedev-iddev-secret")
            )
        );
        (await player.Events(pause, Ct)).Should().BeOfType<OkResult>();
        var playback = AsJson((OkObjectResult)await control.GetHandoff(request.RequestId, Ct));
        playback
            .GetProperty("playback_event_state")
            .GetString()
            .Should()
            .Be("PLAYBACK_STATE_PAUSED");

        var playbackApi = new SonosPlaybackController(
            account,
            development,
            link,
            protocol,
            db,
            locks,
            TimeProvider.System
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var handle = request.RequestId;
        playback.GetProperty("playback_handle").GetGuid().Should().Be(handle);
        upstream.RoomState = "PLAYBACK_STATE_PAUSED";
        var active = AsJson((OkObjectResult)await playbackApi.List(Ct));
        active.GetArrayLength().Should().Be(1);
        active[0].GetProperty("group_name").GetString().Should().Be("Office");
        active[0].GetProperty("playback_state").GetString().Should().Be("PLAYBACK_STATE_PAUSED");
        active[0]
            .GetProperty("current_occurrence")
            .GetProperty("queue_index")
            .GetInt32()
            .Should()
            .Be(1);
        active[0]
            .GetProperty("current_occurrence")
            .GetProperty("track_id")
            .GetString()
            .Should()
            .Be(Track);
        active[0].GetProperty("position_millis").GetInt32().Should().Be(1234);

        (await playbackApi.Command(handle, new("play"), Ct)).Should().BeOfType<AcceptedResult>();
        AsJson((OkObjectResult)await playbackApi.GetPlayback(handle, Ct))
            .GetProperty("playback_state")
            .GetString()
            .Should()
            .Be("PLAYBACK_STATE_PLAYING");
        (await playbackApi.Command(handle, new("pause"), Ct)).Should().BeOfType<AcceptedResult>();
        AsJson((OkObjectResult)await playbackApi.GetPlayback(handle, Ct))
            .GetProperty("playback_state")
            .GetString()
            .Should()
            .Be("PLAYBACK_STATE_PAUSED");

        // Change the room after the ownership read. Sonos must receive the old
        // session ID and get the opportunity to reject it rather than pause that room.
        upstream.EvictOnNextCommand = true;
        (await playbackApi.Command(handle, new("play"), Ct))
            .Should()
            .BeOfType<ConflictObjectResult>();
        (await playbackApi.Command(handle, new("pause"), Ct))
            .Should()
            .BeOfType<ConflictObjectResult>();
        upstream.TransportPaths.Should().Equal("play", "pause", "play");
        AsJson((OkObjectResult)await playbackApi.GetPlayback(handle, Ct))
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("evicted");
        AsJson((OkObjectResult)await playbackApi.List(Ct)).GetArrayLength().Should().Be(0);

        upstream.LoseNextLoadResponse = true;
        var interrupted = request with { RequestId = Guid.CreateVersion7() };
        (await control.Start(interrupted, Ct)).Should().BeOfType<AcceptedAtActionResult>();
        (await control.Start(interrupted, Ct)).Should().BeOfType<AcceptedAtActionResult>();
        var recoveredAfterReconnect = AsJson((OkObjectResult)await playbackApi.List(Ct));
        recoveredAfterReconnect.GetArrayLength().Should().Be(1);
        recoveredAfterReconnect[0]
            .GetProperty("playback_handle")
            .GetGuid()
            .Should()
            .Be(interrupted.RequestId);
        var recovered = AsJson((OkObjectResult)await control.GetHandoff(interrupted.RequestId, Ct));
        recovered.GetProperty("status").GetString().Should().Be("loaded");
        upstream.LoadCount.Should().Be(2);

        await ExerciseFullCommandsAsync(playbackApi, upstream, interrupted.RequestId);

        // Start near the catalog-batch boundary, then ask for the next window.
        upstream.CatalogBatchSizes.Clear();
        upstream.ArtistCatalogUnavailable = true;
        var queueIds = Enumerable
            .Range(0, 5_000)
            .Select(_ => UuidTypeId.Format("track", Guid.NewGuid()))
            .ToArray();
        var longQueue = request with
        {
            RequestId = Guid.CreateVersion7(),
            TrackIds = queueIds,
            StartIndex = 498,
        };
        (await control.Start(longQueue, Ct)).Should().BeOfType<OkObjectResult>();
        upstream.CatalogBatchSizes.Should().Equal(Enumerable.Repeat(500, 10));
        upstream.UnavailableTrack = queueIds[500];
        var unavailable = longQueue with { RequestId = Guid.CreateVersion7() };
        ((ObjectResult)await control.Start(unavailable, Ct)).StatusCode.Should().Be(422);
        upstream.LoadCount.Should().Be(3);
        (await control.GetHandoff(unavailable.RequestId, Ct)).Should().BeOfType<NotFoundResult>();
        var oversized = longQueue with
        {
            RequestId = Guid.CreateVersion7(),
            TrackIds = [.. queueIds, Track],
        };
        ((ObjectResult)await control.Start(oversized, Ct)).StatusCode.Should().Be(422);
        player.Request.Headers.Authorization = upstream
            .Load.GetProperty("httpAuthorization")
            .GetString();
        var first = await ReadWindowAsync(
            player,
            longQueue.RequestId,
            upstream.Load.GetProperty("itemId").GetString()
        );
        first
            .Select(ObjectId)
            .Should()
            .Equal(queueIds.Skip(498).Take(3).Select(id => $"TrackU:{id}"));
        var later = await ReadWindowAsync(
            player,
            longQueue.RequestId,
            first[^1].GetProperty("id").GetString()
        );
        later
            .Select(ObjectId)
            .Should()
            .Equal(queueIds.Skip(500).Take(3).Select(id => $"TrackU:{id}"));

        (
            await player.TimePlayed(
                longQueue.RequestId,
                JsonSerializer.SerializeToElement(new { }),
                Ct
            )
        )
            .Should()
            .BeOfType<NoContentResult>();
        var missingQueue = new SonosQueue
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SessionId = nativeId,
            GroupId = "missing",
            SonosSessionId = "stale",
            Status = "loaded",
            ItemsJson = "[]",
            ExpiresAt = clock.GetUtcNow().AddHours(1),
        };
        db.Add(missingQueue);
        await db.SaveChangesAsync(Ct);
        upstream.MissingRoomStatus = HttpStatusCode.ServiceUnavailable;
        AsJson((OkObjectResult)await playbackApi.List(Ct))
            .EnumerateArray()
            .Select(room => room.GetProperty("playback_handle").GetGuid())
            .Should()
            .Equal(longQueue.RequestId);
        ((ObjectResult)await playbackApi.GetPlayback(missingQueue.Id, Ct))
            .StatusCode.Should()
            .Be(502);
        upstream.MissingRoomStatus = HttpStatusCode.NotFound;
        AsJson((OkObjectResult)await playbackApi.GetPlayback(missingQueue.Id, Ct))
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("evicted");

        await ExerciseHandoffRecoveryAsync(control, upstream, clock, request);

        var pending = AsJson((OkObjectResult)await control.Connect(Ct, "mobile"))
            .GetProperty("authorization_url")
            .GetString()!;
        var pendingState = QueryHelpers.ParseQuery(new Uri(pending).Query)["state"].ToString();
        (await control.Disconnect(Ct)).Should().BeOfType<NoContentResult>();
        AsJson((OkObjectResult)await control.Connection(Ct))
            .GetProperty("linked")
            .GetBoolean()
            .Should()
            .BeFalse();
        (await player.Callback(pendingState, "late-code", Ct))
            .Should()
            .BeOfType<BadRequestObjectResult>();
        (await player.ItemWindow(longQueue.RequestId, null, ct: Ct))
            .Should()
            .BeOfType<NotFoundResult>();
    }

    private static async Task ExerciseHandoffRecoveryAsync(
        SonosControlController control,
        TestUpstream upstream,
        TestClock clock,
        SonosHandoffRequest request
    )
    {
        var creates = upstream.CreateCount;
        var invalid = request with { RequestId = Guid.NewGuid(), PositionMillis = 381_000 };
        ((ObjectResult)await control.Start(invalid, Ct)).StatusCode.Should().Be(422);
        upstream.CreateCount.Should().Be(creates);

        var lostCreate = request with { RequestId = Guid.NewGuid() };
        upstream.LoseNextCreateResponse = true;
        (await control.Start(lostCreate, Ct)).Should().BeOfType<AcceptedAtActionResult>();
        (await control.Start(lostCreate, Ct)).Should().BeOfType<AcceptedAtActionResult>();
        upstream.CreateCount.Should().Be(creates + 1);
        var replacement = request with { RequestId = Guid.NewGuid() };
        (await control.Start(replacement, Ct)).Should().BeOfType<ConflictObjectResult>();

        clock.Advance(TimeSpan.FromSeconds(45));
        HandoffStatus(await control.GetHandoff(lostCreate.RequestId, Ct))
            .Should()
            .Be("interrupted");
        HandoffStatus(await control.Start(replacement, Ct)).Should().Be("loaded");
        HandoffStatus(await control.Start(lostCreate, Ct)).Should().Be("interrupted");
        upstream.CreateCount.Should().Be(creates + 2);

        upstream.RejectNextLoad = true;
        var rejectedLoad = request with { RequestId = Guid.NewGuid() };
        HandoffStatus(await control.Start(rejectedLoad, Ct)).Should().Be("interrupted");
        HandoffStatus(await control.Start(request with { RequestId = Guid.NewGuid() }, Ct))
            .Should()
            .Be("loaded");
    }

    private static string? HandoffStatus(IActionResult result) =>
        AsJson((OkObjectResult)result).GetProperty("status").GetString();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static async Task ExerciseFullCommandsAsync(
        SonosPlaybackController playback,
        TestUpstream upstream,
        Guid handle
    )
    {
        var state = AsJson((OkObjectResult)await playback.GetPlayback(handle, Ct));
        var secondItem = state
            .GetProperty("current_occurrence")
            .GetProperty("item_id")
            .GetString()!;
        var seek = new SonosTransportRequest(
            "seek",
            Guid.NewGuid(),
            secondItem,
            PositionMillis: 59_000
        );
        (await playback.Command(handle, seek, Ct)).Should().BeOfType<AcceptedResult>();
        upstream.RoomPosition.Should().Be(59_000);

        var previous = new SonosTransportRequest("previous", Guid.NewGuid(), secondItem);
        var previousResult = (AcceptedResult)await playback.Command(handle, previous, Ct);
        var firstItem = ReceiptJson(previousResult).GetProperty("target_item_id").GetString()!;
        firstItem.Should().NotBe(secondItem);
        upstream.RoomItem.Should().Be(firstItem);
        (await playback.Command(handle, seek with { CommandId = Guid.NewGuid() }, Ct))
            .Should()
            .BeOfType<ConflictObjectResult>();

        var next = new SonosTransportRequest("next", Guid.NewGuid(), firstItem);
        upstream.LoseNextTransportResponse = true;
        var sendCount = upstream.TransportPaths.Count;
        var lost = (AcceptedResult)await playback.Command(handle, next, Ct);
        ReceiptJson(lost).GetProperty("status").GetString().Should().Be("pending");
        var retried = (AcceptedResult)await playback.Command(handle, next, Ct);
        ReceiptJson(retried).GetProperty("status").GetString().Should().Be("confirmed");
        upstream.TransportPaths.Should().HaveCount(sendCount + 1);
        upstream.RoomItem.Should().Be(secondItem);
        (await playback.Command(handle, next with { Action = "previous" }, Ct))
            .Should()
            .BeOfType<ConflictObjectResult>();

        upstream.RoomMuted = true;
        (await playback.Command(handle, new("volume", Guid.NewGuid(), Volume: 15), Ct))
            .Should()
            .BeOfType<AcceptedResult>();
        var volume = AsJson((OkObjectResult)await playback.GetVolume(handle, Ct));
        volume.GetProperty("volume").GetInt32().Should().Be(15);
        volume.GetProperty("muted").GetBoolean().Should().BeTrue();
        (await playback.Command(handle, new("mute", Guid.NewGuid(), Muted: false), Ct))
            .Should()
            .BeOfType<AcceptedResult>();
        upstream.RoomMuted.Should().BeFalse();
        upstream.RoomVolume.Should().Be(15);
    }

    private static JsonElement ReceiptJson(AcceptedResult response) =>
        JsonSerializer.SerializeToElement(
            response.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
        );

    private static async Task<JsonElement[]> ReadWindowAsync(
        SonosPlayerController player,
        Guid queue,
        string? center
    )
    {
        var result = (JsonResult)
            await player.ItemWindow(queue, center, upcomingWindowSize: 2, ct: Ct);
        var body = JsonSerializer.SerializeToElement(
            result.Value,
            (JsonSerializerOptions)result.SerializerSettings!
        );
        body.GetProperty("includesEndOfQueue").GetBoolean().Should().BeFalse();
        return body.GetProperty("items").EnumerateArray().ToArray();
    }

    private static string? ObjectId(JsonElement item) =>
        item.GetProperty("track").GetProperty("id").GetProperty("objectId").GetString();

    private static JsonElement AsJson(OkObjectResult response) =>
        JsonSerializer.SerializeToElement(
            response.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
        );

    private sealed class ClientFactory(HttpClient catalog) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => catalog;
    }

    internal sealed class TestUpstream : HttpMessageHandler
    {
        public JsonElement Load { get; private set; }
        public int LoadCount { get; private set; }
        public int CreateCount { get; private set; }
        public bool LoseNextCreateResponse { get; set; }
        public bool RejectNextLoad { get; set; }
        public HttpStatusCode MissingRoomStatus { get; set; } = HttpStatusCode.NotFound;
        public Func<string, Task>? BeforeRequest { get; set; }
        public bool LoseNextLoadResponse { get; set; }
        public bool IncompleteToken { get; set; }
        public string? UnavailableTrack { get; set; }
        public bool ArtistCatalogUnavailable { get; set; }
        public List<int> CatalogBatchSizes { get; } = [];
        public List<string> TransportPaths { get; } = [];
        public string RoomState { get; set; } = "PLAYBACK_STATE_PLAYING";
        public bool EvictOnNextCommand { get; set; }
        public bool LoseNextTransportResponse { get; set; }
        public string? RoomItem { get; private set; }
        public int RoomPosition { get; private set; }
        public int RoomVolume { get; private set; } = 17;
        public bool RoomMuted { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (BeforeRequest is { } before)
            {
                await before(path).WaitAsync(ct);
            }

            if (path == "/control/api/v1/groups/missing/playback")
            {
                return new HttpResponseMessage(MissingRoomStatus);
            }

            if (path == "/control/api/v1/groups/office/playbackSession")
            {
                CreateCount++;
                if (LoseNextCreateResponse)
                {
                    LoseNextCreateResponse = false;
                    throw new HttpRequestException("The create response was interrupted.");
                }
            }

            if (path.EndsWith("/loadCloudQueue", StringComparison.Ordinal))
            {
                LoadCount++;
                if (RejectNextLoad)
                {
                    RejectNextLoad = false;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                Load = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                RoomItem = Load.GetProperty("itemId").GetString();
                RoomPosition = Load.GetProperty("positionMillis").GetInt32();
                if (LoseNextLoadResponse)
                {
                    LoseNextLoadResponse = false;
                    throw new HttpRequestException("The load response was interrupted.");
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path == "/control/api/v1/playbackSessions/test-session/playbackSession/skipToItem")
            {
                var command = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var play = command.GetProperty("playOnCompletion").GetBoolean();
                TransportPaths.Add(play ? "play" : "pause");
                if (EvictOnNextCommand)
                {
                    EvictOnNextCommand = false;
                    return new HttpResponseMessage((HttpStatusCode)499)
                    {
                        Content = JsonContent.Create(new { errorCode = "ERROR_SESSION_EVICTED" }),
                    };
                }

                RoomItem = command.GetProperty("itemId").GetString();
                RoomPosition = command.GetProperty("positionMillis").GetInt32();
                RoomState = play ? "PLAYBACK_STATE_PLAYING" : "PLAYBACK_STATE_PAUSED";
                if (LoseNextTransportResponse)
                {
                    LoseNextTransportResponse = false;
                    throw new HttpRequestException("The transport response was interrupted.");
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path == "/control/api/v1/playbackSessions/test-session/playbackSession/seek")
            {
                var command = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                command.GetProperty("itemId").GetString().Should().Be(RoomItem);
                RoomPosition = command.GetProperty("positionMillis").GetInt32();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (
                path.StartsWith(
                    "/control/api/v1/groups/office/groupVolume",
                    StringComparison.Ordinal
                )
                && request.Method == HttpMethod.Post
            )
            {
                var command = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                RoomMuted = command.GetProperty("muted").GetBoolean();
                if (path.EndsWith("/groupVolume", StringComparison.Ordinal))
                {
                    RoomVolume = command.GetProperty("volume").GetInt32();
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path == "/api/v4/catalog/resolve")
            {
                var requestBody = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                const string artistId = "artist_3qmp7zjbg1rpebhtyfy43096vj";
                const string showId = "show_0g9jbeawcz6sqvebcdadr9s070";
                const string sourceId = "source_7bbw2e16a0h9ssczd3bwzgv1wp";
                var references = requestBody.GetProperty("references");
                if (references[0].GetProperty("kind").GetString() == "artist")
                {
                    if (ArtistCatalogUnavailable)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }

                    references.GetArrayLength().Should().Be(1);
                    references[0].GetProperty("id").GetString().Should().Be(artistId);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new
                            {
                                references = new[]
                                {
                                    new
                                    {
                                        id = artistId,
                                        availability = "available",
                                        target = new { kind = "artist", id = artistId },
                                    },
                                },
                                entities = new
                                {
                                    artists = new[]
                                    {
                                        new { id = artistId, name = "Grateful Dead" },
                                    },
                                },
                            }
                        ),
                    };
                }

                var ids = references
                    .EnumerateArray()
                    .Select(reference => reference.GetProperty("id").GetString())
                    .ToArray();
                CatalogBatchSizes.Add(ids.Length);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
                        {
                            references = ids.Select(id => new
                            {
                                id,
                                availability = id == UnavailableTrack ? "unavailable" : "available",
                                target = new { kind = "track", id },
                            }),
                            entities = new
                            {
                                shows = new[] { new { id = showId, display_date = "1977-05-08" } },
                                sources = new[]
                                {
                                    new
                                    {
                                        id = sourceId,
                                        imported_venue = new
                                        {
                                            name = "Barton Hall, Cornell University",
                                        },
                                    },
                                },
                                tracks = ids.Select(id => new
                                {
                                    id,
                                    title = "Test recording",
                                    artist_id = artistId,
                                    show_id = showId,
                                    source_id = sourceId,
                                    duration = 381,
                                    mp3_url = "https://archive.example/track.mp3",
                                }),
                            },
                        }
                    ),
                };
            }

            var content = path switch
            {
                "/login/v3/oauth/access" when IncompleteToken =>
                    """{"access_token":"access","expires_in":3600}""",
                "/login/v3/oauth/access" =>
                    """{"access_token":"access","refresh_token":"refresh","expires_in":3600}""",
                "/control/api/v1/households" =>
                    """{"households":[{"id":"home"},{"id":"studio"}]}""",
                "/control/api/v1/households/home/groups" =>
                    """{"groups":[{"id":"office","name":"Office"}]}""",
                "/control/api/v1/households/studio/groups" =>
                    """{"groups":[{"id":"studio-office","name":"Office"}]}""",
                "/control/api/v1/groups/office/playback" => JsonSerializer.Serialize(
                    new
                    {
                        itemId = RoomItem,
                        positionMillis = RoomPosition,
                        playbackState = RoomState,
                    }
                ),
                "/control/api/v1/groups/office/groupVolume" => JsonSerializer.Serialize(
                    new
                    {
                        volume = RoomVolume,
                        muted = RoomMuted,
                        @fixed = false,
                    }
                ),
                "/control/api/v1/groups/office/playback/subscription" => "{}",
                "/control/api/v1/playbackSessions/test-session/playbackSession/subscription" =>
                    "{}",
                "/control/api/v1/groups/office/playbackSession" =>
                    """{"sessionId":"test-session"}""",
                _ => throw new InvalidOperationException($"Unexpected upstream request {path}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
    }
}
