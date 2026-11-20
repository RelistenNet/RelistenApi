using System.Net;
using System.Net.Http.Json;
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

        using var connect = await Send(
            new(HttpMethod.Post, nativeBase + "/connect?return_to=mobile")
            {
                Headers = { Authorization = new("Bearer", first.Access) },
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
