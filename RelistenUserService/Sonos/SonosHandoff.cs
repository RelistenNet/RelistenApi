using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Identifiers;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

public sealed record SonosHandoffRequest(
    Guid RequestId,
    string GroupId,
    string[] TrackIds,
    int StartIndex,
    int PositionMillis
);

internal sealed record QueueTrack(string Id, QueueTrackMetadata Track);

internal sealed record QueueTrackMetadata(
    MusicObjectId Id,
    string Name,
    int? DurationMillis,
    string ContentType
);

internal sealed record MusicObjectId(string ServiceId, string ObjectId);

internal sealed record SonosGroup(string HouseholdId, string Id, string Name);

internal sealed record CatalogTrack(string Id, string Title, int? Duration, string? Mp3Url);

public sealed class SonosHandoff(
    AccountsDbContext db,
    AdvisoryLockService locks,
    SonosProtocol protocol,
    IHttpClientFactory clients,
    TimeProvider clock
)
{
    internal static readonly JsonSerializerOptions QueueJson = new(JsonSerializerDefaults.Web);

    internal async Task<IReadOnlyList<SonosGroup>> GroupsAsync(string token, CancellationToken ct)
    {
        var result = new List<SonosGroup>();
        var households = await protocol.ControlAsync(token, "households", null, ct);
        foreach (var household in households.GetProperty("households").EnumerateArray())
        {
            var householdId = household.GetProperty("id").GetString()!;
            var response = await protocol.ControlAsync(
                token,
                $"households/{Uri.EscapeDataString(householdId)}/groups",
                null,
                ct
            );
            foreach (var group in response.GetProperty("groups").EnumerateArray())
            {
                result.Add(
                    new(
                        householdId,
                        group.GetProperty("id").GetString()!,
                        group.GetProperty("name").GetString()!
                    )
                );
            }
        }

        return result;
    }

    internal async Task<QueueTrack[]?> ResolveTracksAsync(
        string serviceId,
        string[] trackIds,
        CancellationToken ct
    )
    {
        var catalog = clients.CreateClient("SonosCatalog");
        var resolved = new Dictionary<string, CatalogTrack>(StringComparer.Ordinal);
        foreach (var id in trackIds.Distinct(StringComparer.Ordinal))
        {
            using var response = await catalog.GetAsync($"api/v4/source-tracks/{id}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var track = await response.Content.ReadFromJsonAsync<CatalogTrack>(
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                },
                ct
            );
            if (
                track is null
                || !TrackId.TryParse(track.Id, out _)
                || string.IsNullOrEmpty(track.Mp3Url)
            )
            {
                return null;
            }

            resolved.Add(id, track);
        }

        return trackIds
            .Select(id =>
            {
                var track = resolved[id];
                return new QueueTrack(
                    Guid.CreateVersion7().ToString(),
                    new QueueTrackMetadata(
                        new MusicObjectId(serviceId, $"TrackU:{track.Id}"),
                        track.Title,
                        track.Duration is >= 0 and <= int.MaxValue / 1000
                            ? track.Duration * 1000
                            : null,
                        "audio/mpeg"
                    )
                );
            })
            .ToArray();
    }

    internal async Task<bool> TryPrepareAsync(
        SonosQueue queue,
        string grantVersion,
        CancellationToken ct
    )
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAllAsync(
            [
                $"sonos-group:{queue.GroupId}",
                $"sonos-queue:{queue.Id}",
                $"sonos-grant:{queue.UserId}",
            ],
            ct
        );
        var now = clock.GetUtcNow();
        if (
            await db.Set<SonosQueue>()
                .AnyAsync(
                    row =>
                        row.Id == queue.Id
                        || (
                            row.GroupId == queue.GroupId
                            && row.Status == "dispatching"
                            && row.ExpiresAt > now
                        ),
                    ct
                )
        )
        {
            return false;
        }

        if (
            !await db.Set<SonosGrant>()
                .AnyAsync(
                    row => row.UserId == queue.UserId && row.ProtectedToken == grantVersion,
                    ct
                )
        )
        {
            return false;
        }

        db.Add(queue);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    internal async Task DispatchAsync(
        SonosConfiguration config,
        string token,
        SonosQueue queue,
        string credential,
        string itemId,
        int positionMillis,
        CancellationToken ct
    )
    {
        var groupPath = GroupPath(queue.GroupId);

        // Subscribe before taking over the room so a rejected callback doesn't interrupt playback.
        try
        {
            await protocol.ControlAsync(token, $"{groupPath}/playback/subscription", new { }, ct);
        }
        catch (Exception ex)
            when (ex
                    is SonosRequestException
                        or HttpRequestException
                        or OperationCanceledException
                        or JsonException
            )
        {
            await db.Set<SonosQueue>()
                .Where(row => row.Id == queue.Id && row.Status == "dispatching")
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.Status, "failed"),
                    CancellationToken.None
                );
            throw;
        }
        var result = await protocol.ControlAsync(
            token,
            $"{groupPath}/playbackSession",
            new { appId = "net.relisten.sonos.dev", appContext = queue.Id.ToString("N") },
            ct
        );
        var sessionId = result.TryGetProperty("sessionId", out var direct)
            ? direct.GetString()
            : result.GetProperty("sessionStatus").GetProperty("sessionId").GetString();
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new JsonException("Sonos did not return a playback session ID.");
        }

        await protocol.ControlAsync(
            token,
            $"playbackSessions/{Uri.EscapeDataString(sessionId)}/playbackSession/loadCloudQueue",
            new
            {
                queueBaseUrl = new Uri(config.PublicBase, $"sonos/cloud-queue/{queue.Id}/v2.3/"),
                httpAuthorization = credential,
                useHttpAuthorizationForMedia = false,
                itemId,
                positionMillis,
                playOnCompletion = true,
            },
            ct
        );

        // Account revocation can end a queue while the load is in flight.
        await db.Set<SonosQueue>()
            .Where(row => row.Id == queue.Id && row.Status == "dispatching")
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "loaded"), ct);
    }

    internal async Task ObserveLoadedAsync(string token, SonosQueue queue, CancellationToken ct)
    {
        var status = await protocol.ControlAsync(
            token,
            $"{GroupPath(queue.GroupId)}/playback",
            null,
            ct
        );
        if (
            !status.TryGetProperty("itemId", out var current)
            || current.ValueKind != JsonValueKind.String
        )
        {
            return;
        }

        // Queue-item IDs are generated per occurrence. Seeing one in the room proves it loaded this queue.
        var items = JsonSerializer.Deserialize<QueueTrack[]>(queue.ItemsJson, QueueJson)!;
        if (!items.Any(item => item.Id == current.GetString()))
        {
            return;
        }

        await db.Set<SonosQueue>()
            .Where(row => row.Id == queue.Id && row.Status == "dispatching")
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "loaded"), ct);
    }

    public static string GroupPath(string id) => $"groups/{Uri.EscapeDataString(id)}";
}
