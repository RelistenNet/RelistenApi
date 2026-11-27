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
    public async Task Sonos_queue_edits_preserve_occurrences_and_Temporal_retries_refresh()
    {
        const string sonosOrigin = "https://sonos.example.test";
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        using var upstream = new TestSonosDevelopment.TestUpstream { RejectFirstRefresh = true };
        await using var temporal = await Temporalio.Testing.WorkflowEnvironment.StartLocalAsync();
        await StartApp(
            mountCatalog: false,
            sonos: upstream,
            temporalAddress: temporal.Client.Connection.Options.TargetHost
        );
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
    public async Task Sonos_queue_edit_rolls_back_when_Temporal_is_unreachable()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream, temporalAddress: "127.0.0.1:1");
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
                track_ids = new[] { track },
                start_index = 0,
            }
        );
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var queueUrl = $"{nativeBase}/playback/{handle}/queue";
        using var initial = await Get(queueUrl, login.Access);
        var item = (await Json(initial)).GetProperty("occurrences")[0];
        using var rejected = await SendJson(
            HttpMethod.Put,
            queueUrl,
            login.Access,
            new
            {
                command_id = Guid.NewGuid(),
                expected_version = 1,
                expected_item_id = item.GetProperty("item_id").GetString(),
                occurrences = new[]
                {
                    new
                    {
                        occurrence_id = item.GetProperty("occurrence_id").GetString()!,
                        track_id = track,
                    },
                    new { occurrence_id = "added", track_id = track },
                },
            }
        );
        rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var unchanged = await Get(queueUrl, login.Access);
        var queue = await Json(unchanged);
        queue.GetProperty("queue_version").GetInt64().Should().Be(1);
        queue.GetProperty("occurrences").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task A_signed_household_event_refreshes_names_from_the_authorized_groups_read()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string sonosOrigin = "https://sonos.example.test";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await using var temporal = await Temporalio.Testing.WorkflowEnvironment.StartLocalAsync();
        await StartApp(
            mountCatalog: false,
            sonos: upstream,
            temporalAddress: temporal.Client.Connection.Options.TargetHost,
            sonosHouseholdEvents: true
        );
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, sonosOrigin, login.Access);

        var handle = Guid.NewGuid();
        using var handoff = await SendJson(
            HttpMethod.Post,
            nativeBase + "/handoffs",
            login.Access,
            new
            {
                request_id = handle,
                group_id = "office",
                track_ids = new[] { track },
                start_index = 0,
            }
        );
        handoff.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.OfficeName = "Office + 1";

        using var regroup = new HttpRequestMessage(
            HttpMethod.Post,
            sonosOrigin + "/sonos/events/groups"
        )
        {
            Content = JsonContent.Create(
                new { groups = new[] { new { id = "office", name = "Untrusted" } } }
            ),
        };
        regroup.Headers.Add("X-Sonos-Event-Seq-Id", "9");
        regroup.Headers.Add("X-Sonos-Namespace", "groups");
        regroup.Headers.Add("X-Sonos-Type", "groups");
        regroup.Headers.Add("X-Sonos-Target-Type", "householdId");
        regroup.Headers.Add("X-Sonos-Target-Value", "home");
        regroup.Headers.Add("X-Sonos-Household-Id", "studio");
        regroup.Headers.Add(
            "X-Sonos-Event-Signature",
            WebEncoders.Base64UrlEncode(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        "9groupsgroupshouseholdIdhometest-idtest-secret"
                    )
                )
            )
        );
        using var accepted = await http.SendAsync(regroup);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? observed;
        do
        {
            using var playback = await Get($"{nativeBase}/playback/{handle}", login.Access);
            observed = (await Json(playback)).GetProperty("group_name").GetString();
            if (observed == "Office + 1")
                break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        observed.Should().Be("Office + 1");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Sonos_skip_targets_the_selected_occurrence_and_reconciles_a_lost_response(
        bool legacyQueue
    )
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
        if (legacyQueue)
        {
            // Older snapshots stored the Sonos item ID as their only occurrence identity.
            await using var db = database.CreateContext();
            var stored = await db.Set<SonosQueue>().SingleAsync(row => row.Id == handle);
            stored.ItemsJson = JsonSerializer.Serialize(
                stored
                    .ReadItems()
                    .Select(item => item with { OccurrenceId = null, SourceTrackId = null }),
                SonosHandoff.QueueJson
            );
            await db.SaveChangesAsync();
        }

        var playbackUrl = $"{nativeBase}/playback/{handle}";
        var commandsUrl = playbackUrl + "/commands";
        using var queue = await Get(playbackUrl + "/queue", login.Access);
        var items = (await Json(queue)).GetProperty("occurrences");
        var first = items[0].GetProperty("item_id").GetString()!;
        var last = items[2].GetProperty("item_id").GetString()!;
        var firstOccurrence = items[0].GetProperty("occurrence_id").GetString()!;
        var middleOccurrence = items[1].GetProperty("occurrence_id").GetString()!;
        var lastOccurrence = items[2].GetProperty("occurrence_id").GetString()!;
        firstOccurrence.Should().Be(legacyQueue ? first : "first");
        lastOccurrence.Should().Be(legacyQueue ? last : "last");
        var skip = new
        {
            action = "skip",
            command_id = Guid.NewGuid(),
            expected_item_id = first,
            expected_queue_version = (long?)1,
            target_occurrence_id = lastOccurrence,
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
                target_occurrence_id = firstOccurrence,
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
            .Be(lastOccurrence);
        using var differentTarget = await PostSkip(
            skip with
            {
                target_occurrence_id = middleOccurrence,
            }
        );
        differentTarget.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await differentTarget.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_command_id_conflict");
        upstream.TransportPaths.Should().ContainSingle();
    }

    [Test]
    public async Task Sonos_repeat_reads_the_room_mode_and_recovers_a_lost_reply_without_resending()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, "https://sonos.example.test", login.Access);

        var handle = Guid.NewGuid();
        using var started = await PostSonosHandoff(nativeBase, login.Access, handle);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var playbackUrl = $"{nativeBase}/playback/{handle}";
        var commandsUrl = playbackUrl + "/commands";

        Task<HttpResponseMessage> SetRepeat(string mode, Guid id) =>
            SendJson(
                HttpMethod.Post,
                commandsUrl,
                login.Access,
                new
                {
                    action = "repeat",
                    command_id = id,
                    repeat_mode = mode,
                }
            );

        async Task<JsonElement> ReadPlayback()
        {
            using var response = await Get(playbackUrl, login.Access);
            return await Json(response);
        }

        // An older status response can omit mode and capability fields.
        var initial = await ReadPlayback();
        initial.GetProperty("repeat_mode").ValueKind.Should().Be(JsonValueKind.Null);
        initial.GetProperty("can_repeat").ValueKind.Should().Be(JsonValueKind.Null);

        upstream.RoomPlaybackActions["canRepeat"] = true;
        upstream.RoomPlaybackActions["canRepeatOne"] = false;
        var capabilities = await ReadPlayback();
        capabilities.GetProperty("can_repeat").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("can_repeat_one").GetBoolean().Should().BeFalse();
        using var unavailable = await SetRepeat("one", Guid.NewGuid());
        unavailable.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await unavailable.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString()
            .Should()
            .Be("sonos_command_unavailable");
        using var invalid = await SetRepeat("invalid", Guid.NewGuid());
        ((int)invalid.StatusCode).Should().Be(422);
        upstream.PlayModeWrites.Should().BeEmpty();

        using var all = await SetRepeat("all", Guid.NewGuid());
        all.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await all.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("accepted");
        (await ReadPlayback()).GetProperty("repeat_mode").GetString().Should().Be("all");

        upstream.RoomPlaybackActions["canRepeatOne"] = true;
        upstream.LoseNextPlayModeResponse = true;
        var commandId = Guid.NewGuid();
        using var lost = await SetRepeat("one", commandId);
        lost.StatusCode.Should().Be(HttpStatusCode.Accepted);
        lost.Headers.Location!.OriginalString.Should()
            .Be($"/v1/integrations/sonos/playback/{handle}/commands/{commandId}");
        (await lost.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("pending");
        using var replay = await SetRepeat("one", commandId);
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await replay.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("confirmed");
        (await ReadPlayback()).GetProperty("repeat_mode").GetString().Should().Be("one");

        upstream.RoomPlaybackActions.Clear();
        using var off = await SetRepeat("off", Guid.NewGuid());
        off.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var playback = await ReadPlayback();
        playback.GetProperty("repeat_mode").GetString().Should().Be("off");
        playback.GetProperty("position_millis").GetInt32().Should().Be(1000);
        playback.GetProperty("queue_version").GetInt64().Should().Be(1);

        upstream
            .PlayModeWrites.Select(modes =>
                (
                    modes.GetProperty("repeat").GetBoolean(),
                    modes.GetProperty("repeatOne").GetBoolean()
                )
            )
            .Should()
            .Equal((true, false), (false, true), (false, false));
        upstream.PlayModeWrites.Should().OnlyContain(modes => modes.EnumerateObject().Count() == 2);

        // Another controller can change the effective mode between reads.
        upstream.RoomPlayModes["repeat"] = true;
        (await ReadPlayback()).GetProperty("repeat_mode").GetString().Should().Be("all");
    }

    [TestCase(true, "sonos_command_unavailable")]
    [TestCase(false, "sonos_playback_inactive")]
    public async Task Sonos_repeat_receipt_reports_definitive_rejection(
        bool policyDenial,
        string errorCode
    )
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream
        {
            RejectPlayModeByPolicy = policyDenial,
            LoseNextPlayModeResponse = !policyDenial,
        };
        upstream.RoomPlaybackActions["canRepeat"] = true;
        await StartApp(mountCatalog: false, sonos: upstream);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, "https://sonos.example.test", login.Access);

        var handle = Guid.NewGuid();
        using var started = await PostSonosHandoff(nativeBase, login.Access, handle);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var commandsUrl = $"{nativeBase}/playback/{handle}/commands";
        var commandId = Guid.NewGuid();
        using var submitted = await SendJson(
            HttpMethod.Post,
            commandsUrl,
            login.Access,
            new
            {
                action = "repeat",
                command_id = commandId,
                repeat_mode = "all",
            }
        );
        submitted
            .StatusCode.Should()
            .Be(policyDenial ? HttpStatusCode.Conflict : HttpStatusCode.Accepted);
        if (!policyDenial)
        {
            // The replacement can report the same repeat mode without confirming our command.
            upstream.RoomItem = "replacement-source-item";
        }

        using var receipt = await Get($"{commandsUrl}/{commandId}", login.Access);
        var body = await Json(receipt);
        body.GetProperty("status").GetString().Should().Be("rejected");
        body.GetProperty("code").GetString().Should().Be(errorCode);
        upstream.PlayModeWrites.Should().ContainSingle();
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
