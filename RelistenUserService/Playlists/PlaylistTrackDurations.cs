using System.Text.Json;

namespace RelistenUserService.Playlists;

/// <summary>Captures durations from Catalog without depending on its database or holding write locks.</summary>
public sealed class PlaylistTrackDurations(
    HttpClient client,
    ILogger<PlaylistTrackDurations> logger
)
{
    private const int MaximumReferences = 500;
    private static readonly TimeSpan EditBudget = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyDictionary<string, int>> ResolveAsync(
        IEnumerable<string?> trackIds,
        CancellationToken cancellationToken,
        bool backfill = false
    )
    {
        var durations = new Dictionary<string, int>(StringComparer.Ordinal);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!backfill)
        {
            budget.CancelAfter(EditBudget);
        }

        foreach (
            var ids in trackIds
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Chunk(MaximumReferences)
        )
        {
            try
            {
                await ResolveBatchAsync(ids, durations, budget.Token);
            }
            catch (Exception exception)
                when (!cancellationToken.IsCancellationRequested
                    && exception
                        is HttpRequestException
                            or JsonException
                            or OperationCanceledException
                )
            {
                // Preserve successful batches. Missing durations can be repaired by the backfill.
                logger.LogWarning(
                    exception,
                    "Playlist track duration lookup stopped before all tracks were resolved"
                );
                break;
            }
        }

        return durations;
    }

    private async Task ResolveBatchAsync(
        string[] ids,
        Dictionary<string, int> durations,
        CancellationToken cancellationToken
    )
    {
        using var response = await client.PostAsJsonAsync(
            "api/v4/catalog/resolve",
            new
            {
                references = ids.Select(id => new
                {
                    kind = "track",
                    scheme = "v4",
                    id,
                }),
            },
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        var result =
            await response.Content.ReadFromJsonAsync<ResolveResult>(cancellationToken)
            ?? throw new JsonException("Catalog returned an empty resolve result.");
        if (result.References is null || result.Entities?.Tracks is null)
        {
            throw new JsonException("Catalog returned an incomplete resolve result.");
        }

        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var track in result.Entities.Tracks)
        {
            if (track.Id is { } id && track.Duration is >= 0)
            {
                byId[id] = track.Duration.Value;
            }
        }

        foreach (var reference in result.References)
        {
            if (
                reference.Availability is "available" or "redirected"
                && reference.Id is { } id
                && reference.Target?.Id is { } resolvedId
                && byId.TryGetValue(resolvedId, out var seconds)
            )
            {
                durations[id] = seconds;
            }
        }
    }

    private sealed record ResolveResult(Resolution[]? References, ResolvedEntities? Entities);

    private sealed record Resolution(string? Id, string? Availability, ResolvedTarget? Target);

    private sealed record ResolvedTarget(string? Id);

    private sealed record ResolvedEntities(Track[]? Tracks);

    private sealed record Track(string? Id, int? Duration);
}
