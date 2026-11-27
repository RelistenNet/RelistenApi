using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

[ApiController]
[Route("sonos")]
public sealed class SonosPlayerController(
    AccountsDbContext db,
    SonosAccountLink link,
    TimeProvider clock,
    SonosIntegration integration
) : ControllerBase
{
    private static readonly string[] SignedHeaders =
    [
        "X-Sonos-Event-Seq-Id",
        "X-Sonos-Namespace",
        "X-Sonos-Type",
        "X-Sonos-Target-Type",
        "X-Sonos-Target-Value",
    ];

    private SonosConfiguration? Config =>
        integration.Configuration is { } config
        && config.PublicBase.Authority.Equals(
            Request.Host.Value,
            StringComparison.OrdinalIgnoreCase
        )
            ? config
            : null;

    [HttpGet("oauth/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? state,
        [FromQuery] string? code,
        CancellationToken ct
    )
    {
        if (Config is not { } config)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var result = state is { Length: 43 }
                ? await link.CompleteAsync(config, state, code, ct)
                : null;
            if (result is { ReturnToMobile: true })
            {
                var outcome = result.Connected ? "connected" : "failed";
                return Redirect($"net.relisten.mobile:/sonos/callback?result={outcome}");
            }

            return result is { Connected: true }
                ? Content("Sonos connected. Return to Relisten.", "text/plain")
                : BadRequest("Sonos authorization did not complete.");
        }
        catch (Exception ex)
            when (ex is SonosRequestException or HttpRequestException or JsonException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested)
            )
        {
            return Problem(statusCode: 502, title: "Sonos authorization did not complete.");
        }
    }

    [HttpGet("cloud-queue/{queueId:guid}/v2.3/context")]
    public async Task<IActionResult> Context(Guid queueId, CancellationToken ct) =>
        await AuthorizedQueueAsync(queueId, ct) is not { } queue
            ? NotFound()
            : PlayerJson(
                new
                {
                    contextVersion = "1",
                    queueVersion = queue.QueueVersion.ToString(CultureInfo.InvariantCulture),
                    container = new { name = "Relisten", type = "trackList" },
                    playbackPolicies = new { canShuffle = false, canCrossfade = false },
                }
            );

    [HttpGet("cloud-queue/{queueId:guid}/v2.3/version")]
    public async Task<IActionResult> Version(Guid queueId, CancellationToken ct) =>
        await AuthorizedQueueAsync(queueId, ct) is not { } queue
            ? NotFound()
            : PlayerJson(
                new
                {
                    contextVersion = "1",
                    queueVersion = queue.QueueVersion.ToString(CultureInfo.InvariantCulture),
                }
            );

    [HttpGet("cloud-queue/{queueId:guid}/v2.3/itemWindow")]
    public async Task<IActionResult> ItemWindow(
        Guid queueId,
        [FromQuery] string? itemId,
        [FromQuery] int previousWindowSize = 0,
        [FromQuery] int upcomingWindowSize = 20,
        CancellationToken ct = default
    )
    {
        var queue = await AuthorizedQueueAsync(queueId, ct);
        if (queue is null)
        {
            return NotFound();
        }

        var items = queue.ReadItems().ToList();
        var center = string.IsNullOrEmpty(itemId) ? 0 : items.FindIndex(item => item.Id == itemId);
        string? deletedId = null;
        if (center < 0)
        {
            var removed = queue
                .ReadRemovedItems()
                .ToDictionary(entry => entry.Item.Id, StringComparer.Ordinal);
            if (itemId is null || !removed.TryGetValue(itemId, out var deleted))
            {
                return NotFound();
            }

            // Insert the requested tombstone after its closest surviving predecessor.
            // Later removals can retire that predecessor, so follow their anchors too.
            var afterId = deleted.AfterItemId;
            while (afterId is not null && removed.TryGetValue(afterId, out var predecessor))
            {
                afterId = predecessor.AfterItemId;
            }

            center = afterId is null ? 0 : items.FindIndex(item => item.Id == afterId) + 1;
            items.Insert(center, deleted.Item);
            deletedId = itemId;
        }

        var before = Math.Clamp(previousWindowSize, 0, center);
        var after = Math.Clamp(upcomingWindowSize, 0, items.Count - center - 1);
        var start = center - before;
        var end = center + after + 1;
        return PlayerJson(
            new
            {
                contextVersion = "1",
                queueVersion = queue.QueueVersion.ToString(CultureInfo.InvariantCulture),
                items = items
                    .Skip(start)
                    .Take(end - start)
                    .Select(item => new
                    {
                        item.Id,
                        item.Track,
                        deleted = item.Id == deletedId,
                    }),
                includesBeginningOfQueue = start == 0,
                includesEndOfQueue = end == items.Count,
            }
        );
    }

    [HttpPost("cloud-queue/{queueId:guid}/v2.3/timePlayed")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> TimePlayed(
        Guid queueId,
        [FromBody] JsonElement report,
        CancellationToken ct,
        [FromServices] SonosPlaybackReports? reports = null
    )
    {
        if (await AuthorizedQueueAsync(queueId, ct, acceptFinalReport: true) is not { } queue)
        {
            return NotFound();
        }

        if (reports is not null)
        {
            await reports.RecordAsync(queue, report, ct);
        }
        return NoContent();
    }

    [HttpPost("events/{**path}")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Events(
        [FromBody] JsonElement body,
        CancellationToken ct,
        [FromServices] SonosGroupEvents? groups = null
    )
    {
        if (Config is not { } config)
        {
            return NotFound();
        }

        var fields = SignedHeaders.Select(header => Request.Headers[header].ToString()).ToArray();
        var signature = Request.Headers["X-Sonos-Event-Signature"].ToString();
        var expected = WebEncoders.Base64UrlEncode(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    string.Concat(fields) + config.ClientId + config.ClientSecret
                )
            )
        );
        if (
            fields.Any(string.IsNullOrEmpty)
            || !SonosProtocol.SecretMatches(signature, expected)
            || !long.TryParse(fields[0], out var sequence)
            || sequence < 0
        )
        {
            return Unauthorized();
        }

        var eventNamespace = fields[1];
        var eventType = fields[2];
        var targetType = fields[3];
        var targetId = fields[4];

        // The signed target chooses the household. The unsigned body cannot supply names.
        if (
            eventNamespace == "groups"
            && eventType is ("groups" or "groupInfo")
            && groups is not null
        )
        {
            return await groups.ChangedAsync(targetType, targetId, ct) ? Ok() : StatusCode(503);
        }

        if (
            eventNamespace == "playbackSession"
            && eventType == "sessionError"
            && targetType is "sessionId" or "playbackSessionId"
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("errorCode", out var errorCode)
            && errorCode.ValueKind == JsonValueKind.String
            && errorCode.GetString() == "ERROR_SESSION_EVICTED"
        )
        {
            // The target session is covered by the signature. A group-targeted event
            // cannot by itself prove which session ended.
            await db.Set<SonosQueue>()
                .Where(row =>
                    row.SonosSessionId == targetId
                    && (row.Status == "loaded" || row.Status == "dispatching")
                )
                .EvictAsync(clock.GetUtcNow(), ct);
            return Ok();
        }

        if (
            eventNamespace == "playback"
            && eventType == "playbackStatus"
            && targetType == "groupId"
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("playbackState", out var state)
            && body.TryGetProperty("itemId", out var item)
            && state.ValueKind == JsonValueKind.String
            && state.GetString() is { Length: <= 64 } value
            && item.ValueKind == JsonValueKind.String
            && Guid.TryParse(item.GetString(), out var itemId)
        )
        {
            var now = clock.GetUtcNow();
            var household = Request.Headers["X-Sonos-Household-Id"].ToString();
            var candidates = db.Set<SonosQueue>()
                .Where(row =>
                    row.GroupId == targetId
                    && row.HouseholdId == household
                    && row.ExpiresAt > now
                    && (row.Status == "loaded" || row.Status == "dispatching")
                );
            foreach (var queue in await candidates.AsNoTracking().ToListAsync(ct))
            {
                var items = JsonSerializer.Deserialize<QueueTrack[]>(
                    queue.ItemsJson,
                    SonosHandoff.QueueJson
                )!;
                if (items.Any(entry => entry.Id == itemId.ToString()))
                {
                    await candidates
                        .Where(row =>
                            row.Id == queue.Id
                            && (
                                row.PlaybackEventSequence == null
                                || row.PlaybackEventSequence < sequence
                            )
                        )
                        .ExecuteUpdateAsync(
                            set =>
                                set.SetProperty(row => row.PlaybackEventSequence, sequence)
                                    .SetProperty(row => row.PlaybackEventState, value),
                            ct
                        );
                    break;
                }
            }
        }

        return Ok();
    }

    private async Task<SonosQueue?> AuthorizedQueueAsync(
        Guid id,
        CancellationToken ct,
        bool acceptFinalReport = false
    )
    {
        if (Config is null)
        {
            return null;
        }

        Response.Headers.CacheControl = "no-store";
        var credential = Request.Headers.Authorization.ToString();
        if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            credential = credential["Bearer ".Length..];
        }

        if (credential.Length != 43)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        // The player may submit its final listen after a handoff, eviction, or queue expiry.
        var cutoff = acceptFinalReport ? now.AddMinutes(-5) : now;
        var queue = await db.Set<SonosQueue>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == id
                    && row.ExpiresAt > cutoff
                    && (
                        row.Status == "loaded"
                        || row.Status == "dispatching"
                        || (acceptFinalReport && row.Status == "evicted" && row.EvictedAt > cutoff)
                    ),
                ct
            );
        // The speaker's queue credential inherits the creating login's lifetime.
        return
            queue is not null
            && SonosProtocol.SecretMatches(SonosProtocol.Hash(credential), queue.CredentialHash)
            && await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
            ? queue
            : null;
    }

    private static JsonResult PlayerJson(object value) => new(value, SonosHandoff.QueueJson);
}
