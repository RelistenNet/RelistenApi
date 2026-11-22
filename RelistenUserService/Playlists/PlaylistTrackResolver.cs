using System.Text.Json;
using Relisten.Accounts.Contracts.Playlists;

namespace RelistenUserService.Playlists;

/// <summary>Checks new playlist track references against the Catalog HTTP contract.</summary>
public sealed class PlaylistTrackResolver(HttpClient client)
{
    private const int MaximumReferences = 500;

    internal async Task<HashSet<string>> ReadUnavailableAsync(
        IEnumerable<PlaylistEntryInput> entries,
        CancellationToken cancellationToken
    )
    {
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var batches = entries
            .Select(entry => entry.TrackId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Chunk(MaximumReferences);

        // Playlist locks are held through admission. Bound the total catalog time even
        // when a large playlist spans multiple resolver calls.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var ids in batches)
            {
                var request = new
                {
                    references = ids.Select(id => new
                    {
                        kind = "track",
                        scheme = "v4",
                        id,
                    }),
                };
                using var response = await client.PostAsJsonAsync(
                    "api/v4/catalog/resolve",
                    request,
                    budget.Token
                );
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<ResolveResponse>(
                    budget.Token
                );
                CollectUnavailable(ids, result, unavailable);
            }
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException or JsonException
                || exception is OperationCanceledException
                    && !cancellationToken.IsCancellationRequested
            )
        {
            throw CatalogUnavailable();
        }

        return unavailable;
    }

    private static void CollectUnavailable(
        string[] ids,
        ResolveResponse? response,
        HashSet<string> unavailable
    )
    {
        if (
            response?.References is not { } references
            || references.Length != ids.Length
            || response.Entities?.Tracks is not { } tracks
        )
        {
            throw CatalogUnavailable();
        }

        var playableById = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (
                track?.Id is not { } id
                || track.Playable is not { } playable
                || !playableById.TryAdd(id, playable)
            )
            {
                throw CatalogUnavailable();
            }
        }

        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var reference = references[index];
            if (reference is not { Kind: "track", Scheme: "v4" } || reference.Id != id)
            {
                throw CatalogUnavailable();
            }

            // A v4 track ID either resolves to itself or has no target. Any successful
            // response without enough information to decide must remain retryable.
            if (
                reference is { Availability: "unavailable" or "retired", Target: null }
                && !playableById.ContainsKey(id)
            )
            {
                unavailable.Add(id);
                continue;
            }
            if (
                reference is not { Availability: "available", Target.Kind: "track" }
                || reference.Target.Id != id
                || !playableById.TryGetValue(id, out var playable)
            )
            {
                throw CatalogUnavailable();
            }
            if (!playable)
            {
                unavailable.Add(id);
            }
        }
    }

    private static PlaylistException CatalogUnavailable() =>
        new(
            503,
            "catalog_unavailable",
            "Relisten could not check these tracks. Try again shortly."
        );

    private sealed record ResolveResponse(
        ResolvedReference?[]? References,
        ResolvedEntities? Entities
    );

    private sealed record ResolvedReference(
        string? Kind,
        string? Scheme,
        string? Id,
        string? Availability,
        ReferenceTarget? Target
    );

    private sealed record ReferenceTarget(string? Kind, string? Id);

    private sealed record ResolvedEntities(ResolvedTrack?[]? Tracks);

    private sealed record ResolvedTrack(string? Id, bool? Playable);
}
