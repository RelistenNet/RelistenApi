using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Relisten.Identifiers;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

public sealed record SonosHandoffRequest(
    Guid RequestId,
    string GroupId,
    string[]? TrackIds,
    int StartIndex,
    int PositionMillis,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SonosOccurrenceInput[]? Occurrences = null
);

public sealed record SonosOccurrenceInput(string OccurrenceId, string TrackId);

internal sealed record QueueTrack(
    string Id,
    QueueTrackMetadata Track,
    string? OccurrenceId = null,
    string? SourceTrackId = null
)
{
    [JsonIgnore]
    public string OccurrenceKey => OccurrenceId ?? Id;

    [JsonIgnore]
    public string CatalogTrackId => SourceTrackId ?? Track.Id.ObjectId["TrackU:".Length..];
}

internal sealed record RemovedQueueTrack(QueueTrack Item, string? AfterItemId);

internal sealed record QueueTrackMetadata(
    MusicObjectId Id,
    string Name,
    int? DurationMillis,
    string ContentType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QueueDisplayName? Artist = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QueueDisplayName? Album = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl = null
);

internal sealed record QueueDisplayName(string Name);

internal sealed record MusicObjectId(string ServiceId, string ObjectId);

internal sealed record SonosGroup(string HouseholdId, string Id, string Name);

public sealed class SonosHandoff(
    AccountsDbContext db,
    AdvisoryLockService locks,
    SonosProtocol protocol,
    IHttpClientFactory clients,
    SonosAccountLink link,
    TimeProvider clock,
    ILogger<SonosHandoff> logger
)
{
    internal static readonly JsonSerializerOptions QueueJson = new(JsonSerializerDefaults.Web);
    internal const int MaximumQueueOccurrences = 5_000;

    internal static bool ValidOccurrences(SonosOccurrenceInput[]? entries) =>
        entries is { Length: > 0 and <= MaximumQueueOccurrences }
        && entries.All(entry =>
            entry is not null
            && ValidOccurrenceId(entry.OccurrenceId)
            && TrackId.TryParse(entry.TrackId, out _)
        )
        && entries.Select(entry => entry.OccurrenceId).Distinct(StringComparer.Ordinal).Count()
            == entries.Length;

    internal static bool ValidOccurrenceId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128;

    private const int CatalogBatchSize = 500;
    private static readonly TimeSpan DispatchWindow = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions CatalogJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record CatalogResolution(
        CatalogReference[]? References,
        CatalogEntities? Entities
    );

    private sealed record CatalogReference(string Id, string Availability, CatalogTarget? Target);

    private sealed record CatalogTarget(string Kind, string Id);

    private sealed record CatalogEntities(
        CatalogTrack[]? Tracks,
        CatalogRecording[]? Shows,
        CatalogRecording[]? Sources,
        CatalogArtist[]? Artists
    );

    private sealed record CatalogTrack(
        string Id,
        string Title,
        int? Duration,
        string? Mp3Url,
        string? ArtistId,
        string? ShowId,
        string? SourceId
    );

    private sealed record CatalogRecording(
        string Id,
        string? DisplayDate,
        CatalogVenue? ImportedVenue
    );

    private sealed record CatalogVenue(string? Name);

    private sealed record CatalogArtist(string Id, string Name);

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
        var shows = new Dictionary<string, CatalogRecording>(StringComparer.Ordinal);
        var sources = new Dictionary<string, CatalogRecording>(StringComparer.Ordinal);
        foreach (var batch in trackIds.Distinct(StringComparer.Ordinal).Chunk(CatalogBatchSize))
        {
            var (references, entities) = await ResolveBatchAsync(catalog, "track", batch, ct);
            if (entities.Tracks is null)
            {
                throw new JsonException("Catalog returned no track entities.");
            }

            foreach (var show in entities.Shows ?? [])
            {
                shows.TryAdd(show.Id, show);
            }

            foreach (var source in entities.Sources ?? [])
            {
                sources.TryAdd(source.Id, source);
            }

            var tracksById = entities.Tracks.ToDictionary(
                track => track.Id,
                StringComparer.Ordinal
            );
            foreach (var reference in references)
            {
                if (
                    reference.Availability is not ("available" or "redirected")
                    || reference.Target is not { Kind: "track" } target
                    || !TrackId.TryParse(target.Id, out _)
                    || !tracksById.TryGetValue(target.Id, out var track)
                    || string.IsNullOrEmpty(track.Mp3Url)
                )
                {
                    return null;
                }

                resolved.Add(reference.Id, track);
            }
        }

        var artists = await ResolveArtistNamesAsync(catalog, resolved.Values, ct);

        return trackIds
            .Select(id =>
            {
                var track = resolved[id];
                var show = track.ShowId is { } showKey ? shows.GetValueOrDefault(showKey) : null;
                var source = track.SourceId is { } sourceKey
                    ? sources.GetValueOrDefault(sourceKey)
                    : null;
                var date = show?.DisplayDate ?? source?.DisplayDate;
                var venue = source?.ImportedVenue?.Name ?? show?.ImportedVenue?.Name;
                var album = string.Join(
                    " · ",
                    new[] { date, venue }.Where(value => !string.IsNullOrWhiteSpace(value))
                );
                var imageUrl = ShowId.TryParse(track.ShowId, out var showId)
                    ? $"https://relisten.net/album-art/{showId.Value:D}.png"
                    : null;
                return new QueueTrack(
                    Guid.CreateVersion7().ToString(),
                    new QueueTrackMetadata(
                        new MusicObjectId(serviceId, $"TrackU:{track.Id}"),
                        track.Title,
                        track.Duration is >= 0 and <= int.MaxValue / 1000
                            ? track.Duration * 1000
                            : null,
                        "audio/mpeg",
                        DisplayName(
                            track.ArtistId is { } artistKey
                                ? artists.GetValueOrDefault(artistKey)
                                : null
                        ),
                        DisplayName(album),
                        imageUrl
                    )
                );
            })
            .ToArray();
    }

    private async Task<Dictionary<string, string>> ResolveArtistNamesAsync(
        HttpClient catalog,
        IEnumerable<CatalogTrack> tracks,
        CancellationToken ct
    )
    {
        var artists = new Dictionary<string, string>(StringComparer.Ordinal);
        var artistIds = tracks
            .Select(track => track.ArtistId)
            .OfType<string>()
            .Where(id => ArtistId.TryParse(id, out _))
            .Distinct(StringComparer.Ordinal);
        foreach (var batch in artistIds.Chunk(CatalogBatchSize))
        {
            (CatalogReference[] references, CatalogEntities entities) result;
            try
            {
                result = await ResolveBatchAsync(catalog, "artist", batch, ct);
            }
            catch (Exception error)
                when (!ct.IsCancellationRequested
                    && error is HttpRequestException or JsonException or OperationCanceledException
                )
            {
                // The recordings already passed playback validation. Artist names are display-only.
                logger.LogWarning(error, "Could not resolve Sonos artist names");
                break;
            }

            var names = (result.entities.Artists ?? []).ToDictionary(
                artist => artist.Id,
                artist => artist.Name,
                StringComparer.Ordinal
            );
            foreach (var reference in result.references)
            {
                if (
                    reference.Target is { Kind: "artist" } target
                    && names.TryGetValue(target.Id, out var name)
                )
                {
                    artists.TryAdd(reference.Id, name);
                }
            }
        }

        return artists;
    }

    private static async Task<(
        CatalogReference[] References,
        CatalogEntities Entities
    )> ResolveBatchAsync(HttpClient catalog, string kind, string[] batch, CancellationToken ct)
    {
        using var response = await catalog.PostAsJsonAsync(
            "api/v4/catalog/resolve",
            new
            {
                references = batch.Select(id => new
                {
                    kind,
                    scheme = "v4",
                    id,
                }),
            },
            ct
        );
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CatalogResolution>(CatalogJson, ct);
        if (
            result?.References is null
            || result.Entities is null
            || !batch.SequenceEqual(result.References.Select(reference => reference.Id))
        )
        {
            throw new JsonException("Catalog returned an incomplete Sonos resolve batch.");
        }

        return (result.References, result.Entities);
    }

    private static QueueDisplayName? DisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Sonos limits album and artist names to 76 characters.
        const int limit = 76;
        var length =
            value.Length > limit && char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        return new QueueDisplayName(value[..Math.Min(value.Length, length)]);
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
                            && row.DispatchUntil > now
                        ),
                    ct
                )
        )
        {
            return false;
        }

        if (
            !await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct)
            || !await db.Set<SonosGrant>()
                .AnyAsync(
                    row => row.UserId == queue.UserId && row.ProtectedToken == grantVersion,
                    ct
                )
        )
        {
            return false;
        }

        queue.DispatchUntil = now + DispatchWindow;
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
            await EndDispatchAsync(queue.Id, "failed");
            throw;
        }
        var sessionId = await OpenSessionAsync(token, queue, ct);
        if (sessionId is null)
        {
            return;
        }

        var sessionPath = SonosProtocol.SessionPath(sessionId);
        // A lost subscription can be recovered by reading the room before each command.
        try
        {
            await protocol.ControlAsync(
                token,
                $"{sessionPath}/playbackSession/subscription",
                new { },
                ct
            );
        }
        catch (Exception error)
            when (!ct.IsCancellationRequested
                && error
                    is SonosRequestException
                        or HttpRequestException
                        or JsonException
                        or OperationCanceledException
            )
        {
            logger.LogWarning(error, "Could not subscribe to Sonos session events");
        }

        await LoadQueueAsync(
            config,
            token,
            queue,
            credential,
            itemId,
            positionMillis,
            sessionPath,
            ct
        );
    }

    private async Task<string?> OpenSessionAsync(
        string token,
        SonosQueue queue,
        CancellationToken ct
    )
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockDestinationAsync(queue, ct);
            if (!await CanDispatchAsync(queue, "failed", ct))
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            var result = await protocol.ControlAsync(
                token,
                $"{GroupPath(queue.GroupId)}/playbackSession",
                new { appId = "net.relisten.sonos", appContext = queue.Id.ToString("N") },
                ct
            );
            var sessionId = ReadSessionId(result);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new JsonException("Sonos did not return a playback session ID.");
            }

            // Persist before load so discovery can recover an acknowledged load with a lost response.
            await db.Set<SonosQueue>()
                .Where(row => row.Id == queue.Id && row.Status == "dispatching")
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.SonosSessionId, sessionId),
                    ct
                );
            await transaction.CommitAsync(ct);
            return sessionId;
        }
        catch (SonosRequestException error)
            when (error.Status
                    is HttpStatusCode.BadRequest
                        or HttpStatusCode.Unauthorized
                        or HttpStatusCode.Forbidden
                        or HttpStatusCode.NotFound
                        or HttpStatusCode.Gone
                || error.Code is "ERROR_INVALID_OBJECT_ID" or "ERROR_INVALID_PARAMETER"
            )
        {
            await EndDispatchAsync(queue.Id, "failed");
            throw;
        }
    }

    private static string? ReadSessionId(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (
            result.TryGetProperty("sessionStatus", out var status)
            && status.ValueKind == JsonValueKind.Object
        )
        {
            result = status;
        }

        return
            result.TryGetProperty("sessionId", out var sessionId)
            && sessionId.ValueKind == JsonValueKind.String
            ? sessionId.GetString()
            : null;
    }

    private async Task LoadQueueAsync(
        SonosConfiguration config,
        string token,
        SonosQueue queue,
        string credential,
        string itemId,
        int positionMillis,
        string sessionPath,
        CancellationToken ct
    )
    {
        // Disconnect serializes with each effect. The player can still read the prepared
        // snapshot while the advisory lock is held.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockDestinationAsync(queue, ct);
        if (!await CanDispatchAsync(queue, "interrupted", ct))
        {
            await transaction.CommitAsync(ct);
            return;
        }

        try
        {
            await protocol.ControlAsync(
                token,
                $"{sessionPath}/playbackSession/loadCloudQueue",
                new
                {
                    queueBaseUrl = new Uri(
                        config.PublicBase,
                        $"sonos/cloud-queue/{queue.Id}/v2.3/"
                    ),
                    httpAuthorization = credential,
                    useHttpAuthorizationForMedia = false,
                    itemId,
                    positionMillis,
                    playOnCompletion = true,
                },
                ct
            );
        }
        catch (SonosRequestException error) when (error.SessionEnded)
        {
            await EndDispatchAsync(queue.Id, "evicted");
            await transaction.CommitAsync(CancellationToken.None);
            throw;
        }
        catch (SonosRequestException error)
            when (error.Status
                    is HttpStatusCode.BadRequest
                        or HttpStatusCode.Unauthorized
                        or HttpStatusCode.Forbidden
                        or HttpStatusCode.NotFound
                || error.Code is "ERROR_INVALID_PARAMETER" or "ERROR_DISALLOWED_BY_POLICY"
            )
        {
            // Create already succeeded. A declined load releases the room but cannot
            // promise that the source playing before create is still there.
            await EndDispatchAsync(queue.Id, "interrupted");
            await transaction.CommitAsync(CancellationToken.None);
            throw;
        }

        await db.Set<SonosQueue>()
            .Where(row => row.Id == queue.Id && row.Status == "dispatching")
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "loaded"), ct);
        await transaction.CommitAsync(ct);
    }

    private Task LockDestinationAsync(SonosQueue queue, CancellationToken ct) =>
        locks.LockAllAsync([$"sonos-group:{queue.GroupId}", $"sonos-grant:{queue.UserId}"], ct);

    private async Task<bool> CanDispatchAsync(
        SonosQueue queue,
        string invalidSessionOutcome,
        CancellationToken ct
    )
    {
        // Disconnect, relink and rejected refresh revoke the queue under the grant lock.
        // Native logout uses a separate transaction, so check the initiating session here too.
        var now = clock.GetUtcNow();
        if (
            !await db.Set<SonosQueue>()
                .AnyAsync(
                    row =>
                        row.Id == queue.Id
                        && row.Status == "dispatching"
                        && row.DispatchUntil > now
                        && row.ExpiresAt > now,
                    ct
                )
        )
        {
            return false;
        }

        if (await link.SessionActiveAsync(queue.UserId, queue.SessionId, ct))
        {
            return true;
        }

        await EndDispatchAsync(queue.Id, invalidSessionOutcome);
        return false;
    }

    private Task<int> EndDispatchAsync(Guid id, string status) =>
        db.Set<SonosQueue>()
            .Where(row => row.Id == id && row.Status == "dispatching")
            .ExecuteUpdateAsync(
                set => set.SetProperty(row => row.Status, status),
                CancellationToken.None
            );

    internal async Task ObserveLoadedAsync(string token, SonosQueue queue, CancellationToken ct)
    {
        var status = await protocol.ControlAsync(
            token,
            $"{GroupPath(queue.GroupId)}/playback",
            null,
            ct
        );
        if (
            status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("itemId", out var current)
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
