using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Relisten.Identifiers;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

[ApiController]
[Route("v1/integrations/sonos")]
[Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
public sealed class SonosControlController(
    CurrentAccountContext account,
    SonosDevelopment development,
    SonosAccountLink link,
    SonosHandoff handoff,
    AccountsDbContext db,
    TimeProvider clock
) : ControllerBase
{
    private const int MaximumQueueOccurrences = 5_000;

    private SonosConfiguration? Config => development.Configuration;

    [HttpPost("connect")]
    public async Task<IActionResult> Connect(CancellationToken ct)
    {
        if (Config is not { } config)
        {
            return NotFound();
        }

        var url = await link.BeginAsync(config, account.User.Id, account.NativeSessionId, ct);
        return Ok(new { authorizationUrl = url });
    }

    [HttpGet("connection")]
    public async Task<IActionResult> Connection(CancellationToken ct)
    {
        if (Config is null)
        {
            return NotFound();
        }

        return Ok(new { linked = await link.IsLinkedAsync(account.User.Id, ct) });
    }

    [HttpGet("groups")]
    public async Task<IActionResult> Groups(CancellationToken ct)
    {
        if (Config is not { } config)
        {
            return NotFound();
        }

        try
        {
            var token = await link.AccessAsync(config, account.User.Id, ct);
            return token is null
                ? Conflict(new { code = "sonos_reconnect_required" })
                : Ok(await handoff.GroupsAsync(token.Token, ct));
        }
        catch (Exception ex) when (SonosUnavailable(ex, ct))
        {
            return Problem(statusCode: 502, title: "Could not read Sonos groups.");
        }
    }

    [HttpPost("handoffs")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Start(SonosHandoffRequest request, CancellationToken ct)
    {
        if (Config is not { } config)
        {
            return NotFound();
        }

        if (
            request.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.GroupId)
            || request.GroupId.Length > 128
            || request.TrackIds is not { Length: > 0 and <= MaximumQueueOccurrences }
            || request.TrackIds.Any(id => !TrackId.TryParse(id, out _))
            || request.StartIndex < 0
            || request.StartIndex >= request.TrackIds.Length
            || request.PositionMillis < 0
        )
        {
            return Problem(statusCode: 422, title: "Invalid local Sonos handoff.");
        }

        var requestHash = SonosProtocol.Hash(JsonSerializer.Serialize(request));
        var previous = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == request.RequestId, ct);
        if (previous is not null)
        {
            return
                previous.UserId == account.User.Id
                && previous.RequestHash == requestHash
                && await link.SessionActiveAsync(previous.UserId, previous.SessionId, ct)
                ? Receipt(previous)
                : Conflict();
        }

        SonosQueue? prepared = null;
        try
        {
            var token = await link.AccessAsync(config, account.User.Id, ct);
            if (token is null)
            {
                return Conflict(new { code = "sonos_reconnect_required" });
            }

            var groups = await handoff.GroupsAsync(token.Token, ct);
            var group = groups.SingleOrDefault(group => group.Id == request.GroupId);
            if (group is null)
            {
                return NotFound();
            }

            var items = await handoff.ResolveTracksAsync(config.ServiceId, request.TrackIds, ct);
            if (items is null)
            {
                return Problem(
                    statusCode: 422,
                    title: "Every track in the local test must be streamable."
                );
            }

            var credential = SonosProtocol.NewSecret();
            var queue = new SonosQueue
            {
                Id = request.RequestId,
                UserId = account.User.Id,
                SessionId = account.NativeSessionId,
                GroupId = group.Id,
                GroupName = group.Name,
                HouseholdId = group.HouseholdId,
                RequestHash = requestHash,
                CredentialHash = SonosProtocol.Hash(credential),
                ItemsJson = JsonSerializer.Serialize(items, SonosHandoff.QueueJson),
                ExpiresAt = clock.GetUtcNow().AddHours(2),
            };
            if (!await handoff.TryPrepareAsync(queue, token.GrantVersion, ct))
            {
                var existing = await ReadQueueAsync(queue.Id, ct);
                return
                    existing is not null
                    && existing.RequestHash == requestHash
                    && await link.SessionActiveAsync(existing.UserId, existing.SessionId, ct)
                    ? Receipt(existing)
                    : Conflict(new { code = "sonos_handoff_busy" });
            }

            prepared = queue;
            await handoff.DispatchAsync(
                config,
                token.Token,
                queue,
                credential,
                items[request.StartIndex].Id,
                request.PositionMillis,
                ct
            );
            return Receipt(await ReadQueueAsync(queue.Id, ct) ?? queue);
        }
        catch (Exception ex) when (SonosUnavailable(ex, ct))
        {
            return prepared is null
                ? Problem(statusCode: 502, title: "Could not prepare the Sonos handoff.")
                : Receipt(await ReadQueueAsync(prepared.Id, ct) ?? prepared);
        }
    }

    [HttpGet("handoffs/{queueId:guid}")]
    public async Task<IActionResult> GetHandoff(Guid queueId, CancellationToken ct)
    {
        if (Config is not { } config)
        {
            return NotFound();
        }

        var queue = await ReadQueueAsync(queueId, ct);
        if (queue is null || !await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct))
        {
            return NotFound();
        }

        if (queue.Status == "dispatching")
        {
            try
            {
                var token = await link.AccessAsync(config, account.User.Id, ct);
                if (token is not null)
                {
                    await handoff.ObserveLoadedAsync(token.Token, queue, ct);
                }

                queue = await ReadQueueAsync(queue.Id, ct) ?? queue;
            }
            catch (Exception ex) when (SonosUnavailable(ex, ct))
            {
                // The receipt remains pending when the room cannot confirm which queue loaded.
            }
        }

        return Receipt(queue);
    }

    private Task<SonosQueue?> ReadQueueAsync(Guid id, CancellationToken ct) =>
        db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id && row.UserId == account.User.Id, ct);

    private IActionResult Receipt(SonosQueue queue)
    {
        if (queue.ExpiresAt <= clock.GetUtcNow())
        {
            return Problem(statusCode: 410, title: "The local Sonos handoff expired.");
        }

        var result = new
        {
            requestId = queue.Id,
            playbackHandle = queue.SonosSessionId is not null && queue.Status == "loaded"
                ? (Guid?)queue.Id
                : null,
            status = queue.Status,
            playbackEventState = queue.PlaybackEventState,
        };
        return queue.Status == "dispatching"
            ? AcceptedAtAction(nameof(GetHandoff), new { queueId = queue.Id }, result)
            : Ok(result);
    }

    private static bool SonosUnavailable(Exception ex, CancellationToken ct) =>
        ex is SonosRequestException or HttpRequestException or JsonException or KeyNotFoundException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);
}
