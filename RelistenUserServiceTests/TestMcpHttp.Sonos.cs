using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Relisten.Identifiers;
using RelistenUserService.Sonos;
using Temporalio.Activities;

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

        var removedPlaylist = PlaylistId.New().Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await EnsureSonosCatalogTrack(connection, TrackId.Parse(track).Value);
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
                    playlist_id = key == "b" ? removedPlaylist : null,
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
        using var removedReport = await SendSonosReport(
            playerUrl + "/timePlayed",
            credential,
            Guid.NewGuid(),
            b,
            195_000
        );
        removedReport.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (
            await connection.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM user_data.plays WHERE playlist_id = @removedPlaylist",
                new { removedPlaylist }
            )
        )
            .Should()
            .Be(1);

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

    [TestCase(false)]
    [TestCase(true)]
    public async Task Sonos_queue_edit_rolls_back_when_Temporal_cannot_confirm_the_signal(
        bool stalls
    )
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        using var blackhole = stalls ? new TcpListener(IPAddress.Loopback, 0) : null;
        blackhole?.Start();
        var address = blackhole?.LocalEndpoint.ToString() ?? "127.0.0.1:1";
        await StartApp(mountCatalog: false, sonos: upstream, temporalAddress: address);
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
        var elapsed = Stopwatch.StartNew();
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
            )
            .WaitAsync(TimeSpan.FromSeconds(5));
        rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        if (blackhole is not null)
        {
            elapsed
                .Elapsed.Should()
                .BeGreaterThan(TimeSpan.FromMilliseconds(750))
                .And.BeLessThan(TimeSpan.FromSeconds(3));
            using var accepted = await blackhole
                .AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        using var unchanged = await Get(queueUrl, login.Access);
        var queue = await Json(unchanged);
        queue.GetProperty("queue_version").GetInt64().Should().Be(1);
        queue.GetProperty("occurrences").GetArrayLength().Should().Be(1);
    }

    [TestCase("householdId", null)]
    [TestCase("groupId", null)]
    [TestCase("groupId", "{}")]
    [TestCase("groupId", """{"groups":[{"id":"office","name":1}]}""")]
    public async Task A_signed_groups_event_refreshes_linked_accounts_in_the_target_household(
        string targetType,
        string? malformed
    )
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string sonosOrigin = "https://sonos.example.test";
        using var upstream = new TestSonosDevelopment.TestUpstream();
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
        using var otherCode = await Authorize(fields, await SignIn(fields, "google-shared"));
        var other = await Exchange(Native, NativeRedirect, ReadCode(otherCode));
        await LinkSonos(nativeBase, sonosOrigin, other.Access);

        var subscriptions = new ConcurrentQueue<string>();
        upstream.BeforeRequest = path =>
        {
            if (path.EndsWith("/groups/subscription", StringComparison.Ordinal))
            {
                subscriptions.Enqueue(path);
            }

            return Task.CompletedTask;
        };
        var homeQueues = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var studioQueue = Guid.NewGuid();
        using var office = await PostSonosHandoff(nativeBase, login.Access, homeQueues[0]);
        using var studio = await PostSonosHandoff(
            nativeBase,
            login.Access,
            studioQueue,
            "studio-office"
        );
        upstream.CreatedSessionId = "other-session";
        using var otherOffice = await PostSonosHandoff(nativeBase, other.Access, homeQueues[1]);
        foreach (var handoff in new[] { office, studio, otherOffice })
        {
            handoff.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(handoff)).GetProperty("status").GetString().Should().Be("loaded");
        }
        subscriptions.Should().Contain("/control/api/v1/households/home/groups/subscription");
        subscriptions.Should().Contain("/control/api/v1/households/studio/groups/subscription");

        upstream.OfficeName = "Office + 1";
        upstream.StudioName = "Studio + 1";
        var releaseRetries = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (malformed is not null)
        {
            upstream.NextHomeGroupsResponse = malformed;
            upstream.BeforeRequest = path =>
                path == "/control/api/v1/households/home/groups"
                && ActivityExecutionContext.HasCurrent
                && ActivityExecutionContext.Current.Info.Attempt > 1
                    ? releaseRetries.Task
                    : Task.CompletedTask;
        }

        try
        {
            using var accepted = await SendSonosEvent(
                sonosOrigin,
                "groups",
                "groups",
                targetType,
                targetType == "groupId" ? "office" : "home",
                new { groups = new[] { new { id = "office", name = "Untrusted" } } },
                unsignedHousehold: "studio"
            );
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var connection = new NpgsqlConnection(database.ConnectionString);
            if (malformed is not null)
            {
                // The healthy account must refresh even while retry attempts are blocked.
                await WaitForChangedRoomNames(connection, homeQueues, expected: 1);
                releaseRetries.TrySetResult();
            }

            await WaitForChangedRoomNames(connection, homeQueues, expected: 2);
            (
                await connection.QuerySingleAsync<string>(
                    "SELECT group_name FROM user_data.sonos_queues WHERE id = @studioQueue",
                    new { studioQueue }
                )
            )
                .Should()
                .Be("Office");
        }
        finally
        {
            releaseRetries.TrySetResult();
        }
    }

    private static async Task WaitForChangedRoomNames(
        NpgsqlConnection connection,
        Guid[] homeQueues,
        int expected
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        int updated;
        do
        {
            updated = await connection.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM user_data.sonos_queues WHERE id = ANY(@homeQueues) AND group_name = 'Office + 1'",
                new { homeQueues }
            );
            if (updated == expected)
            {
                return;
            }
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        updated.Should().Be(expected);
    }

    private async Task<HttpResponseMessage> SendSonosEvent(
        string origin,
        string eventNamespace,
        string type,
        string targetType,
        string target,
        object body,
        string? unsignedHousehold = null
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            origin + "/sonos/events/" + eventNamespace
        )
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Sonos-Event-Seq-Id", "9");
        request.Headers.Add("X-Sonos-Namespace", eventNamespace);
        request.Headers.Add("X-Sonos-Type", type);
        request.Headers.Add("X-Sonos-Target-Type", targetType);
        request.Headers.Add("X-Sonos-Target-Value", target);
        if (unsignedHousehold is not null)
        {
            request.Headers.Add("X-Sonos-Household-Id", unsignedHousehold);
        }
        var signed = System.Text.Encoding.UTF8.GetBytes(
            $"9{eventNamespace}{type}{targetType}{target}test-idtest-secret"
        );
        request.Headers.Add(
            "X-Sonos-Event-Signature",
            WebEncoders.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(signed))
        );
        return await http.SendAsync(request);
    }

    [Test]
    public async Task Sonos_counts_each_qualified_report_on_the_saved_playlist_occurrence_once()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string origin = "https://sonos.example.test";
        var trackUuid = Guid.NewGuid();
        var track = UuidTypeId.Format("track", trackUuid);
        var playlist = PlaylistId.New();
        var entry = PlaylistEntryId.New();
        var otherPlaylist = PlaylistId.New();
        var otherEntry = PlaylistEntryId.New();
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, origin, login.Access);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await EnsureSonosCatalogTrack(connection, trackUuid);

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
                position_millis = 0,
                occurrences = new[]
                {
                    new
                    {
                        occurrence_id = "first",
                        track_id = track,
                        playlist_id = (string?)otherPlaylist.Value,
                        playlist_entry_id = (string?)otherEntry.Value,
                    },
                    new
                    {
                        occurrence_id = "target",
                        track_id = track,
                        playlist_id = (string?)playlist.Value,
                        playlist_entry_id = (string?)entry.Value,
                    },
                    new
                    {
                        occurrence_id = "no-playlist",
                        track_id = track,
                        playlist_id = (string?)null,
                        playlist_entry_id = (string?)null,
                    },
                },
            }
        );
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        using var queue = await Get($"{nativeBase}/playback/{handle}/queue", login.Access);
        var occurrences = (await Json(queue)).GetProperty("occurrences");
        var item = occurrences[1].GetProperty("item_id").GetString()!;
        var credential = upstream.Load.GetProperty("httpAuthorization").GetString()!;
        var timePlayed = $"{origin}/sonos/cloud-queue/{handle}/v2.3/timePlayed";
        async Task Report(
            Guid reportId,
            string id,
            int millis,
            HttpStatusCode status = HttpStatusCode.NoContent
        )
        {
            using var response = await SendSonosReport(
                timePlayed,
                credential,
                reportId,
                id,
                millis
            );
            response.StatusCode.Should().Be(status);
        }

        var first = Guid.NewGuid();
        await Report(first, item, 20_000);
        (
            await connection.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM user_data.plays WHERE playlist_id = @id",
                new { id = playlist.Value }
            )
        )
            .Should()
            .Be(0);
        await AttemptReportWhileOwnerDisabled(handle, first, item);

        // A rejected catalog insert must leave the same report eligible for retry.
        await connection.ExecuteAsync(
            "ALTER TABLE public.source_track_plays ADD CONSTRAINT reject_sonos_report CHECK (app_type <> 3) NOT VALID"
        );
        try
        {
            await Report(first, item, 195_000, HttpStatusCode.InternalServerError);
        }
        finally
        {
            await connection.ExecuteAsync(
                "ALTER TABLE public.source_track_plays DROP CONSTRAINT reject_sonos_report"
            );
        }

        await Report(first, item, 195_000);
        await Report(Guid.NewGuid(), item, 200_000);
        await Report(first, item, 210_000);
        await Report(Guid.NewGuid(), occurrences[2].GetProperty("item_id").GetString()!, 200_000);

        var saved = await connection.QueryAsync<(
            string? PlaylistId,
            string? EntryId,
            short AppType
        )>(
            "SELECT playlist_id, playlist_entry_id, app_type FROM user_data.plays WHERE track_id = @track",
            new { track }
        );
        saved
            .Should()
            .BeEquivalentTo(
                new (string?, string?, short)[]
                {
                    (playlist.Value, entry.Value, 3),
                    (playlist.Value, entry.Value, 3),
                    (null, null, 3),
                }
            );
        var catalog = await connection.QueryAsync<string?>(
            "SELECT playlist_id FROM public.source_track_plays WHERE source_track_uuid = @trackUuid AND app_type = 3",
            new { trackUuid }
        );
        catalog.Should().BeEquivalentTo(new[] { playlist.Value, playlist.Value, null });
    }

    private async Task AttemptReportWhileOwnerDisabled(Guid queueId, Guid reportId, string itemId)
    {
        // Exercise the race after the callback has authorized the session and read the queue.
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = database.CreateContext();
        var queue = await db.Set<SonosQueue>().AsNoTracking().SingleAsync(row => row.Id == queueId);
        var owner = await db.Users.SingleAsync(row => row.Id == queue.UserId);
        owner.Status = "disabled";
        await db.SaveChangesAsync();

        try
        {
            var recorder = scope.ServiceProvider.GetRequiredService<SonosPlaybackReports>();
            await recorder.RecordAsync(
                queue,
                SonosReportBody(reportId, itemId, 195_000),
                CancellationToken.None
            );
        }
        finally
        {
            owner.Status = "active";
            await db.SaveChangesAsync();
        }
    }

    [TestCase("replacement")]
    [TestCase("event")]
    [TestCase("expiry")]
    public async Task Sonos_accepts_a_final_report_during_grace_without_reopening_the_queue(
        string endedBy
    )
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        const string sonosOrigin = "https://sonos.example.test";
        const string track = "track_4rc5e1za2bvsrnnr0edzzj8rfs";
        var playlist = PlaylistId.New().Value;
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, sonosOrigin, login.Access);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await EnsureSonosCatalogTrack(connection, TrackId.Parse(track).Value);

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
                occurrences = new[]
                {
                    new
                    {
                        occurrence_id = "first",
                        track_id = track,
                        playlist_id = playlist,
                    },
                },
            }
        );
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = upstream.Load.GetProperty("itemId").GetString()!;
        var credential = upstream.Load.GetProperty("httpAuthorization").GetString()!;
        var playerUrl = $"{sonosOrigin}/sonos/cloud-queue/{handle}/v2.3";
        var reportId = Guid.NewGuid();

        switch (endedBy)
        {
            case "replacement":
                upstream.CreatedSessionId = "next-session";
                using (
                    var replacement = await PostSonosHandoff(
                        nativeBase,
                        login.Access,
                        Guid.NewGuid()
                    )
                )
                {
                    replacement.StatusCode.Should().Be(HttpStatusCode.OK);
                }
                var otherCredential = upstream.Load.GetProperty("httpAuthorization").GetString()!;
                using (
                    var wrongQueue = await SendSonosReport(
                        playerUrl + "/timePlayed",
                        otherCredential,
                        reportId,
                        item,
                        195_000
                    )
                )
                {
                    wrongQueue.StatusCode.Should().Be(HttpStatusCode.NotFound);
                }
                break;
            case "event":
                using (
                    var evicted = await SendSonosEvent(
                        sonosOrigin,
                        "playbackSession",
                        "sessionError",
                        "sessionId",
                        upstream.CreatedSessionId,
                        new { errorCode = "ERROR_SESSION_EVICTED" }
                    )
                )
                {
                    evicted.StatusCode.Should().Be(HttpStatusCode.OK);
                }
                break;
            case "expiry":
                await connection.ExecuteAsync(
                    "UPDATE user_data.sonos_queues SET expires_at = now() - interval '30 seconds' WHERE id = @handle",
                    new { handle }
                );
                break;
        }

        using var version = await Get(playerUrl + "/version", credential);
        version.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var final = await SendSonosReport(
            playerUrl + "/timePlayed",
            credential,
            reportId,
            item,
            195_000
        );
        final.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (
            await connection.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM user_data.plays WHERE playlist_id = @playlist",
                new { playlist }
            )
        )
            .Should()
            .Be(1);

        var endedColumn = endedBy == "expiry" ? "expires_at" : "evicted_at";
        await connection.ExecuteAsync(
            $"UPDATE user_data.sonos_queues SET {endedColumn} = now() - interval '10 minutes' WHERE id = @handle",
            new { handle }
        );
        using var tooLate = await SendSonosReport(
            playerUrl + "/timePlayed",
            credential,
            Guid.NewGuid(),
            item,
            195_000
        );
        tooLate.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static JsonElement SonosReportBody(Guid reportId, string id, int millis) =>
        JsonSerializer.SerializeToElement(
            new
            {
                items = new[]
                {
                    new
                    {
                        reportId,
                        id,
                        durationPlayedMillis = millis,
                    },
                },
            }
        );

    private Task<HttpResponseMessage> SendSonosReport(
        string url,
        string credential,
        Guid reportId,
        string id,
        int millis
    ) => SendJson(HttpMethod.Post, url, credential, SonosReportBody(reportId, id, millis));

    private static Task<int> EnsureSonosCatalogTrack(NpgsqlConnection connection, Guid trackUuid)
    {
        return connection.ExecuteAsync(
            """
            WITH artist AS (
                INSERT INTO public.artists (uuid, name, slug, updated_at) VALUES (@artistUuid, 'Sonos', @slug, now()) RETURNING id
            )
            INSERT INTO public.source_tracks (uuid, artist_id, track_position, title, slug, mp3_url, duration, updated_at)
            SELECT @trackUuid, id, 1, 'Sonos fixture', 'sonos-fixture', 'https://example.test/sonos.mp3', 381, now() FROM artist
            ON CONFLICT (uuid) DO NOTHING
            """,
            new
            {
                trackUuid,
                artistUuid = Guid.NewGuid(),
                slug = "sonos-" + Guid.NewGuid().ToString("N"),
            }
        );
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

    private Task<HttpResponseMessage> PostSonosHandoff(
        string nativeBase,
        string token,
        Guid id,
        string groupId = "office"
    ) =>
        Send(
            new(HttpMethod.Post, nativeBase + "/handoffs")
            {
                Headers = { Authorization = new("Bearer", token) },
                Content = JsonContent.Create(
                    new
                    {
                        request_id = id,
                        group_id = groupId,
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
