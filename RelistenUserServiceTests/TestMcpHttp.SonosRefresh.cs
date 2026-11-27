using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Dapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using RelistenUserService.Persistence;
using RelistenUserService.Sonos;
using Temporalio.Testing;

namespace RelistenUserServiceTests;

public sealed partial class TestMcpHttp
{
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task A_Sonos_refresh_records_the_delivery_outcome(HttpStatusCode firstStatus)
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await using var temporal = await WorkflowEnvironment.StartLocalAsync();
        await StartApp(
            mountCatalog: false,
            sonos: upstream,
            temporalAddress: temporal.Client.Connection.Options.TargetHost
        );
        var login = await SignInForSonos(nativeBase);
        var handle = Guid.NewGuid();
        using var loaded = await PostSonosHandoff(nativeBase, login.Access, handle);
        loaded.StatusCode.Should().Be(HttpStatusCode.OK);

        var requests = new ConcurrentQueue<(string? Token, TimeSpan Elapsed)>();
        var timer = Stopwatch.StartNew();
        upstream.OnRefresh = request =>
        {
            requests.Enqueue((request.Headers.Authorization?.Parameter, timer.Elapsed));
            if (requests.Count > 1)
            {
                return new(HttpStatusCode.OK);
            }

            // The token was issued for an hour. The provider invalidates it
            // before that deadline and expects its replacement on the retry.
            upstream.AccessToken = "renewed";
            var response = new HttpResponseMessage(firstStatus);
            if (firstStatus == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(4));
            }
            return response;
        };

        var queueUrl = $"{nativeBase}/playback/{handle}/queue";
        using var initial = await Get(queueUrl, login.Access);
        var item = (await Json(initial)).GetProperty("occurrences")[0];
        var request = new
        {
            command_id = Guid.NewGuid(),
            expected_version = 1,
            expected_item_id = item.GetProperty("item_id").GetString(),
            occurrences = new[]
            {
                new
                {
                    occurrence_id = item.GetProperty("occurrence_id").GetString()!,
                    track_id = item.GetProperty("track_id").GetString(),
                },
                new { occurrence_id = "new", track_id = item.GetProperty("track_id").GetString() },
            },
        };
        using var edit = await SendJson(HttpMethod.Put, queueUrl, login.Access, request);
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        await temporal
            .Client.GetWorkflowHandle($"sonos-queue-refresh-{handle}")
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(15));

        using var queue = await Get(queueUrl, login.Access);
        if (firstStatus == HttpStatusCode.NotFound)
        {
            queue.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        else
        {
            (await Json(queue)).GetProperty("refresh_pending").GetBoolean().Should().BeFalse();
        }
        await using var databaseRead = database.CreateContext();
        var stored = await databaseRead
            .Set<SonosQueue>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == handle);
        if (firstStatus is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
        {
            stored.RefreshedQueueVersion.Should().Be(2);
            var attempts = requests.ToArray();
            attempts.Should().HaveCount(2);
            if (firstStatus == HttpStatusCode.Unauthorized)
            {
                attempts.Select(attempt => attempt.Token).Should().Equal("access", "renewed");
            }
            else
            {
                (attempts[1].Elapsed - attempts[0].Elapsed)
                    .Should()
                    .BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3.75));
            }
        }
        else
        {
            stored.RefreshedQueueVersion.Should().Be(1);
            requests.Should().ContainSingle();
        }

        using var replay = await SendJson(HttpMethod.Put, queueUrl, login.Access, request);
        (await Json(replay)).GetProperty("queue_version").GetInt64().Should().Be(2);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task A_signaled_queue_edit_is_delivered_only_after_commit(bool commit)
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await using var temporal = await WorkflowEnvironment.StartLocalAsync();
        await StartApp(
            mountCatalog: false,
            sonos: upstream,
            temporalAddress: temporal.Client.Connection.Options.TargetHost
        );
        var login = await SignInForSonos(nativeBase);
        var handle = Guid.NewGuid();
        using var loaded = await PostSonosHandoff(nativeBase, login.Access, handle);
        loaded.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var writer = database.CreateContext();
        await using var edit = await writer.Database.BeginTransactionAsync();
        var backend = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
        // Hold only the room lock. The activity can read the grant up front, so the
        // wait here proves delivery itself is fenced from the uncommitted edit.
        await new AdvisoryLockService(writer).LockAsync(
            "sonos-group:office",
            CancellationToken.None
        );
        await writer
            .Set<SonosQueue>()
            .Where(row => row.Id == handle)
            .ExecuteUpdateAsync(update =>
                update
                    .SetProperty(row => row.QueueVersion, 2)
                    .SetProperty(row => row.RefreshAfter, DateTimeOffset.UtcNow)
            );

        var starter = new SonosWorkflowStarter(
            temporal.Client,
            NullLogger<SonosWorkflowStarter>.Instance
        );
        (await starter.TrySignalQueueAsync(handle, CancellationToken.None)).Should().BeTrue();
        await WaitForDeliveryToTakeRoomLock(backend);
        upstream.RefreshCount.Should().Be(0);

        if (commit)
        {
            await edit.CommitAsync();
        }
        else
        {
            await edit.RollbackAsync();
        }
        await temporal
            .Client.GetWorkflowHandle($"sonos-queue-refresh-{handle}")
            .GetResultAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        upstream.RefreshCount.Should().Be(commit ? 1 : 0);
    }

    [Test]
    public async Task A_changed_grant_does_not_fail_a_cutover_refresh()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);
        var login = await SignInForSonos(nativeBase);
        var handle = Guid.NewGuid();
        using var loaded = await PostSonosHandoff(nativeBase, login.Access, handle);
        loaded.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var writer = database.CreateContext();
        var queue = await writer
            .Set<SonosQueue>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == handle);
        await writer
            .Set<SonosQueue>()
            .Where(row => row.Id == handle)
            .ExecuteUpdateAsync(update =>
                update
                    .SetProperty(row => row.QueueVersion, 2)
                    .SetProperty(row => row.RefreshAfter, DateTimeOffset.UtcNow)
            );
        var grant = writer.Set<SonosGrant>().Where(row => row.UserId == queue.UserId);
        var original = await grant.Select(row => row.ProtectedToken).SingleAsync();
        await using var rotation = await writer.Database.BeginTransactionAsync();
        var backend = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
        await new AdvisoryLockService(writer).LockAsync(
            "sonos-group:office",
            CancellationToken.None
        );

        await using var scope = app.Services.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<SonosIntegration>().Configuration!;
        var refresh = scope.ServiceProvider.GetRequiredService<SonosQueueRefresh>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = refresh.RefreshDuringCutoverAsync(config, handle, deadline.Token);
        try
        {
            await WaitForDeliveryToTakeRoomLock(backend);
            await grant.ExecuteUpdateAsync(update =>
                update.SetProperty(row => row.ProtectedToken, original + "-rotated")
            );
            await rotation.CommitAsync();
            await pending.WaitAsync(deadline.Token);
            upstream.RefreshCount.Should().Be(0);
        }
        finally
        {
            await rotation.DisposeAsync();
            await grant.ExecuteUpdateAsync(update =>
                update.SetProperty(row => row.ProtectedToken, original)
            );
        }

        await refresh.RefreshDuringCutoverAsync(config, handle, deadline.Token);
        upstream.RefreshCount.Should().Be(1);
    }

    [Test]
    public async Task A_failed_cutover_attempt_cannot_reschedule_delivery_after_it_ended()
    {
        const string nativeBase = AccountsOrigin + "/v1/integrations/sonos";
        using var upstream = new TestSonosDevelopment.TestUpstream();
        await StartApp(mountCatalog: false, sonos: upstream);
        var login = await SignInForSonos(nativeBase);
        var handle = Guid.NewGuid();
        using var loaded = await PostSonosHandoff(nativeBase, login.Access, handle);
        loaded.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var writer = database.CreateContext();
        var queue = writer.Set<SonosQueue>().Where(row => row.Id == handle);
        await queue.ExecuteUpdateAsync(update =>
            update
                .SetProperty(row => row.QueueVersion, 2)
                .SetProperty(row => row.RefreshAfter, DateTimeOffset.UtcNow)
        );
        upstream.OnRefresh = _ => new(HttpStatusCode.ServiceUnavailable);
        upstream.BeforeRequest = async path =>
        {
            if (path.EndsWith("/refreshCloudQueue", StringComparison.Ordinal))
            {
                // Set up the terminal state a Temporal attempt can commit before the
                // cutover poller writes its retry time outside the delivery transaction.
                await queue.ExecuteUpdateAsync(update =>
                    update.SetProperty(row => row.RefreshAfter, (DateTimeOffset?)null)
                );
            }
        };

        await using var scope = app.Services.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<SonosIntegration>().Configuration!;
        await scope
            .ServiceProvider.GetRequiredService<SonosQueueRefresh>()
            .RefreshDuringCutoverAsync(config, handle, CancellationToken.None);
        using var stored = await Get($"{nativeBase}/playback/{handle}/queue", login.Access);
        (await Json(stored)).GetProperty("refresh_pending").GetBoolean().Should().BeFalse();
    }

    private async Task<TokenPair> SignInForSonos(string nativeBase)
    {
        var fields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        using var code = await Authorize(fields, await SignIn(fields));
        var login = await Exchange(Native, NativeRedirect, ReadCode(code));
        await LinkSonos(nativeBase, "https://sonos.example.test", login.Access);
        return login;
    }

    private async Task WaitForDeliveryToTakeRoomLock(int backend)
    {
        await using var observer = new NpgsqlConnection(database.ConnectionString);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (
            !await observer.QuerySingleAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM pg_locks
                        WHERE locktype = 'advisory' AND NOT granted AND @backend = ANY(pg_blocking_pids(pid))
                    )
                    """,
                    new { backend },
                    cancellationToken: deadline.Token
                )
            )
        )
        {
            await Task.Delay(30, deadline.Token);
        }
    }
}
