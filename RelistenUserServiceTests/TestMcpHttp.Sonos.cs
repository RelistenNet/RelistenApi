using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RelistenUserService.Sonos;

namespace RelistenUserServiceTests;

public sealed partial class TestMcpHttp
{
    [Test]
    public async Task Sonos_handle_and_window_require_the_creating_session_to_remain_active()
    {
        const string sonosOrigin = "https://sonos.example.test";
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);

        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        var cookie = await SignIn(fields);
        using var firstCode = await Authorize(fields, cookie);
        var first = await Exchange(Native, NativeRedirect, ReadCode(firstCode));

        await LinkSonos(nativeBase, sonosOrigin, first.Access);

        var id = Guid.NewGuid();
        using var started = await PostSonosHandoff(nativeBase, first.Access, id);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(started)).GetProperty("playback_handle").GetGuid().Should().Be(id);
        var handleUrl = nativeBase + "/playback/" + id;
        var windowUrl = sonosOrigin + $"/sonos/cloud-queue/{id}/v2.3/itemWindow";
        var credential = upstream.Load.GetProperty("httpAuthorization").GetString()!;

        using var secondCode = await Authorize(fields, cookie);
        var second = await Exchange(Native, NativeRedirect, ReadCode(secondCode));
        using var otherCode = await Authorize(fields, await SignIn(fields, "google-shared"));
        var other = await Exchange(Native, NativeRedirect, ReadCode(otherCode));

        using var sameAccount = await Get(handleUrl, second.Access);
        sameAccount.StatusCode.Should().Be(HttpStatusCode.OK);
        using var differentAccount = await Get(handleUrl, other.Access);
        differentAccount.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var noBearer = await http.GetAsync(handleUrl);
        noBearer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var window = await Get(windowUrl, credential);
        window.StatusCode.Should().Be(HttpStatusCode.OK);
        using var playerAsController = await Get(handleUrl, credential);
        playerAsController.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var commandId = Guid.NewGuid();
        using var command = await Send(
            new(HttpMethod.Post, handleUrl + "/commands")
            {
                Headers = { Authorization = new("Bearer", second.Access) },
                Content = JsonContent.Create(
                    new
                    {
                        action = "pause",
                        command_id = commandId,
                        expected_item_id = upstream.Load.GetProperty("itemId").GetString(),
                    }
                ),
            }
        );
        command.StatusCode.Should().Be(HttpStatusCode.Accepted);
        command
            .Headers.Location!.OriginalString.Should()
            .Be($"/v1/integrations/sonos/playback/{id}/commands/{commandId}");

        await SignOutNative(first.Access);
        using var inaccessible = await Get(handleUrl, second.Access);
        inaccessible.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var revokedWindow = await Get(windowUrl, credential);
        revokedWindow.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var creates = upstream.CreateCount;
        var loads = upstream.LoadCount;
        using var refused = await RevokeDuringHandoff(
            upstream,
            nativeBase,
            second.Access,
            "/api/v4/catalog/resolve"
        );
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        upstream.CreateCount.Should().Be(creates);
        upstream.LoadCount.Should().Be(loads);

        using var thirdCode = await Authorize(fields, cookie);
        var third = await Exchange(Native, NativeRedirect, ReadCode(thirdCode));
        using var interrupted = await RevokeDuringHandoff(
            upstream,
            nativeBase,
            third.Access,
            "/control/api/v1/playbackSessions/test-session/playbackSession/subscription"
        );
        interrupted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(interrupted)).GetProperty("status").GetString().Should().Be("interrupted");
        upstream.CreateCount.Should().Be(creates + 1);
        upstream.LoadCount.Should().Be(loads);

        using var fourthCode = await Authorize(fields, cookie);
        var fourth = await Exchange(Native, NativeRedirect, ReadCode(fourthCode));
        await VerifyExpiredHandoffCannotTakeOver(upstream, nativeBase, fourth.Access);
    }

    [Test]
    public async Task Sonos_queue_edits_preserve_occurrences_and_retry_refresh()
    {
        const string sonosOrigin = "https://sonos.example.test";
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        using var upstream = new TestSonosDevelopment.TestUpstream { RejectFirstRefresh = true };
        await StartApp(mountCatalog: false, sonos: upstream);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, sonosOrigin, login.Access);

        var handle = Guid.NewGuid();
        var queueUrl = $"{nativeBase}/playback/{handle}/queue";
        var playerUrl = $"{sonosOrigin}/sonos/cloud-queue/{handle}/v2.3";
        using var started = await SendJson(
            HttpMethod.Post,
            nativeBase + "/handoffs",
            login.Access,
            new
            {
                request_id = handle,
                group_id = "office",
                start_index = 0,
                position_millis = 1000,
                occurrences = new[] { "a", "b", "c" }.Select(key => new
                {
                    occurrence_id = key,
                    track_id = track,
                }),
            }
        );
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var credential = upstream.Load.GetProperty("httpAuthorization").GetString()!;
        using var initial = await Get(queueUrl, login.Access);
        var oldItems = (await Json(initial)).GetProperty("occurrences");
        var a = oldItems[0].GetProperty("item_id").GetString()!;
        var b = oldItems[1].GetProperty("item_id").GetString()!;
        var c = oldItems[2].GetProperty("item_id").GetString()!;
        a.Should().NotBe(b);

        var edit = new
        {
            command_id = Guid.NewGuid(),
            expected_version = 1,
            expected_item_id = a,
            occurrences = new[] { "a", "c", "d" }.Select(key => new
            {
                occurrence_id = key,
                track_id = track,
            }),
        };
        upstream.RoomItem = b;
        using var moved = await SendJson(HttpMethod.Put, queueUrl, login.Access, edit);
        moved.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await moved.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_playback_changed");
        upstream.RoomItem = a;
        using var saved = await SendJson(HttpMethod.Put, queueUrl, login.Access, edit);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(saved)).GetProperty("queue_version").GetInt64().Should().Be(2);
        using var replay = await SendJson(HttpMethod.Put, queueUrl, login.Access, edit);
        (await Json(replay)).GetProperty("queue_version").GetInt64().Should().Be(2);
        using var stale = await SendJson(
            HttpMethod.Put,
            queueUrl,
            login.Access,
            edit with
            {
                command_id = Guid.NewGuid(),
            }
        );
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_queue_changed");
        using var protectedCurrent = await SendJson(
            HttpMethod.Put,
            queueUrl,
            login.Access,
            edit with
            {
                command_id = Guid.NewGuid(),
                expected_version = 2,
                occurrences = edit.occurrences.Skip(1),
            }
        );
        (await protectedCurrent.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_queue_current_protected");

        using var edited = await Get(queueUrl, login.Access);
        var items = (await Json(edited)).GetProperty("occurrences");
        items
            .EnumerateArray()
            .Select(item => item.GetProperty("occurrence_id").GetString())
            .Should()
            .Equal("a", "c", "d");
        items[0].GetProperty("item_id").GetString().Should().Be(a);
        items[1].GetProperty("item_id").GetString().Should().Be(c);
        using var version = await Get(playerUrl + "/version", credential);
        (await Json(version)).GetProperty("queueVersion").GetString().Should().Be("2");
        using var deletedAnchor = await Get(
            playerUrl + "/itemWindow?itemId=" + b + "&previousWindowSize=1",
            credential
        );
        var playerItems = (await Json(deletedAnchor)).GetProperty("items");
        playerItems[0].GetProperty("id").GetString().Should().Be(a);
        playerItems[1].GetProperty("id").GetString().Should().Be(b);
        playerItems[1].GetProperty("deleted").GetBoolean().Should().BeTrue();
        playerItems[2].GetProperty("id").GetString().Should().Be(c);

        using var staleNext = await SendJson(
            HttpMethod.Post,
            $"{nativeBase}/playback/{handle}/commands",
            login.Access,
            new
            {
                action = "next",
                command_id = Guid.NewGuid(),
                expected_item_id = a,
                expected_queue_version = 1,
            }
        );
        staleNext.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleNext.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_queue_changed");
        upstream.TransportPaths.Should().BeEmpty();

        upstream.RoomItem = b;
        using var removedItem = await Get($"{nativeBase}/playback/{handle}", login.Access);
        (await Json(removedItem)).GetProperty("status").GetString().Should().Be("unconfirmed");
        upstream.RoomItem = c;
        using var successor = await Get($"{nativeBase}/playback/{handle}", login.Access);
        (await Json(successor))
            .GetProperty("current_occurrence")
            .GetProperty("occurrence_id")
            .GetString()
            .Should()
            .Be("c");

        var deadline = DateTime.UtcNow.AddSeconds(18);
        bool refreshPending;
        do
        {
            using var observed = await Get(queueUrl, login.Access);
            refreshPending = (await Json(observed)).GetProperty("refresh_pending").GetBoolean();
            if (refreshPending)
            {
                await Task.Delay(250);
            }
        } while (refreshPending && DateTime.UtcNow < deadline);
        refreshPending.Should().BeFalse();
        upstream.RefreshCount.Should().Be(2);
        upstream.LoadCount.Should().Be(1);
        upstream.RoomPosition.Should().Be(1000);
    }

    [Test]
    public async Task Sonos_skip_targets_the_selected_occurrence_and_reconciles_a_lost_response()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, "https://sonos.example.test", login.Access);

        var handle = Guid.NewGuid();
        using var started = await SendJson(
            HttpMethod.Post,
            nativeBase + "/handoffs",
            login.Access,
            new
            {
                request_id = handle,
                group_id = "office",
                start_index = 0,
                position_millis = 1000,
                occurrences = new[] { "first", "middle", "last" }.Select(key => new
                {
                    occurrence_id = key,
                    track_id = track,
                }),
            }
        );
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var playbackUrl = $"{nativeBase}/playback/{handle}";
        var commandsUrl = playbackUrl + "/commands";
        using var queue = await Get(playbackUrl + "/queue", login.Access);
        var items = (await Json(queue)).GetProperty("occurrences");
        var first = items[0].GetProperty("item_id").GetString()!;
        var last = items[2].GetProperty("item_id").GetString()!;
        var skip = new
        {
            action = "skip",
            command_id = Guid.NewGuid(),
            expected_item_id = first,
            expected_queue_version = (long?)1,
            target_occurrence_id = "last",
        };

        Task<HttpResponseMessage> PostSkip(object body) =>
            SendJson(HttpMethod.Post, commandsUrl, login.Access, body);

        using var missingVersion = await PostSkip(skip with { expected_queue_version = null });
        ((int)missingVersion.StatusCode).Should().Be(422);
        using var stale = await PostSkip(skip with { expected_queue_version = 2 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_queue_changed");
        using var missingTarget = await PostSkip(skip with { target_occurrence_id = "missing" });
        missingTarget.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await missingTarget.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_command_unavailable");

        using var current = await PostSkip(
            skip with
            {
                command_id = Guid.NewGuid(),
                target_occurrence_id = "first",
            }
        );
        current.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await current.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("confirmed");
        upstream.RoomPosition.Should().Be(1000);
        upstream.TransportPaths.Should().BeEmpty();

        upstream.RoomState = "PLAYBACK_STATE_PAUSED";
        upstream.LoseNextTransportResponse = true;
        using var lost = await PostSkip(skip);
        lost.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await lost.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("pending");
        using var retry = await PostSkip(skip);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var receipt = await retry.Content.ReadFromJsonAsync<JsonElement>();
        receipt.GetProperty("status").GetString().Should().Be("confirmed");
        receipt.GetProperty("target_item_id").GetString().Should().Be(last);
        upstream.TransportPaths.Should().Equal("pause");
        upstream.RoomPosition.Should().Be(0);
        using var playback = await Get(playbackUrl, login.Access);
        (await Json(playback))
            .GetProperty("current_occurrence")
            .GetProperty("occurrence_id")
            .GetString()
            .Should()
            .Be("last");
        using var differentTarget = await PostSkip(skip with { target_occurrence_id = "middle" });
        differentTarget.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await differentTarget.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_command_id_conflict");
        upstream.TransportPaths.Should().ContainSingle();
    }

    private Task<HttpResponseMessage> SendJson(
        HttpMethod method,
        string url,
        string token,
        object body
    ) =>
        Send(
            new HttpRequestMessage(method, url)
            {
                Headers = { Authorization = new("Bearer", token) },
                Content = JsonContent.Create(body),
            }
        );

    private async Task LinkSonos(string nativeBase, string sonosOrigin, string token)
    {
        using var connect = await Send(
            new(HttpMethod.Post, nativeBase + "/connect?return_to=mobile")
            {
                Headers = { Authorization = new("Bearer", token) },
            }
        );
        var authorization = new Uri(
            (await Json(connect)).GetProperty("authorization_url").GetString()!
        );
        var state = QueryHelpers.ParseQuery(authorization.Query)["state"].ToString();
        using var callback = await http.GetAsync(
            QueryHelpers.AddQueryString(
                sonosOrigin + "/sonos/oauth/callback",
                new Dictionary<string, string?> { ["state"] = state, ["code"] = "fake-sonos-code" }
            )
        );
        callback
            .Headers.Location!.OriginalString.Should()
            .Be("net.relisten.mobile:/sonos/callback?result=connected");
    }

    private async Task VerifyExpiredHandoffCannotTakeOver(
        TestSonosDevelopment.TestUpstream upstream,
        string nativeBase,
        string token
    )
    {
        var subscriptionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var finishSubscription = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        upstream.BeforeRequest = path =>
        {
            if (path != "/control/api/v1/groups/office/playback/subscription")
            {
                return Task.CompletedTask;
            }

            subscriptionStarted.TrySetResult();
            return finishSubscription.Task;
        };

        var creates = upstream.CreateCount;
        var expiredId = Guid.NewGuid();
        var lateHandoff = PostSonosHandoff(nativeBase, token, expiredId);
        try
        {
            await subscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            upstream.BeforeRequest = null;
            await using var db = database.CreateContext();
            await db.Set<SonosQueue>()
                .Where(queue => queue.Id == expiredId)
                .ExecuteUpdateAsync(update =>
                    update.SetProperty(
                        queue => queue.DispatchUntil,
                        DateTimeOffset.UtcNow.AddSeconds(-1)
                    )
                );
            using var replacement = await PostSonosHandoff(nativeBase, token, Guid.NewGuid());
            replacement.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(replacement)).GetProperty("status").GetString().Should().Be("loaded");
        }
        finally
        {
            upstream.BeforeRequest = null;
            finishSubscription.TrySetResult();
        }

        using var late = await lateHandoff;
        late.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(late)).GetProperty("status").GetString().Should().Be("interrupted");
        upstream.CreateCount.Should().Be(creates + 1);
    }

    private Task<HttpResponseMessage> PostSonosHandoff(string nativeBase, string token, Guid id) =>
        Send(
            new(HttpMethod.Post, nativeBase + "/handoffs")
            {
                Headers = { Authorization = new("Bearer", token) },
                Content = JsonContent.Create(
                    new
                    {
                        request_id = id,
                        group_id = "office",
                        track_ids = new[] { "track_4rc5e1za2bvsrnnr0edzzj8rfs" },
                        start_index = 0,
                        position_millis = 1000,
                    }
                ),
            }
        );

    private async Task SignOutNative(string token)
    {
        using var logout = await Send(
            new(HttpMethod.Post, AccountsOrigin + "/v1/logout")
            {
                Headers = { Authorization = new("Bearer", token) },
            }
        );
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<HttpResponseMessage> RevokeDuringHandoff(
        TestSonosDevelopment.TestUpstream upstream,
        string nativeBase,
        string token,
        string pauseAt
    )
    {
        var upstreamReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var resumeUpstream = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        upstream.BeforeRequest = path =>
        {
            if (path != pauseAt)
            {
                return Task.CompletedTask;
            }

            upstreamReached.TrySetResult();
            return resumeUpstream.Task;
        };

        var pending = PostSonosHandoff(nativeBase, token, Guid.NewGuid());
        try
        {
            await upstreamReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SignOutNative(token);
        }
        finally
        {
            upstream.BeforeRequest = null;
            resumeUpstream.TrySetResult();
        }

        return await pending;
    }
}
