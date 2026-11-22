using System.Text.Json;
using Npgsql;

namespace RelistenUserService.Search;

internal sealed class MergedSearch(
    CatalogSearchClient catalog,
    PlaylistSearchCandidates candidates,
    PlaylistSearchCards cards,
    ILogger<MergedSearch> logger
)
{
    public async Task<MergedSearchResponse> Run(
        MergedSearchRequest request,
        Func<CancellationToken, Task<UserId?>>? viewer,
        CancellationToken cancellationToken
    )
    {
        var catalogTask = request.WantsCatalog
            ? SearchCatalog(request, cancellationToken)
            : Task.FromResult(new CatalogResults("not_requested", null));
        var playlistTask = request.WantsPlaylists
            ? SearchPlaylists(request, viewer, cancellationToken)
            : Task.FromResult(new PlaylistResults("not_requested", [], null));

        await Task.WhenAll(catalogTask, playlistTask);
        var index = await catalogTask;
        var playlists = await playlistTask;
        return new(
            request.Q ?? "",
            request.RequestId,
            index.Availability,
            playlists.Availability,
            index.Body,
            playlists.Cards,
            request.Page,
            playlists.NextPage
        );
    }

    private async Task<CatalogResults> SearchCatalog(
        MergedSearchRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await catalog.Get(request, cancellationToken);
            if (
                result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("availability", out var availability)
                || availability.ValueKind != JsonValueKind.String
            )
            {
                return new("unavailable", null);
            }
            return new(availability.GetString()!, result);
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            // Query text and index result bodies can contain private listener input.
            logger.LogWarning("Catalog search failed ({ErrorType})", error.GetType().Name);
            return new("unavailable", null);
        }
    }

    private async Task<PlaylistResults> SearchPlaylists(
        MergedSearchRequest request,
        Func<CancellationToken, Task<UserId?>>? viewer,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var owner = viewer is null ? null : await viewer(deadline.Token);
            var hits = await candidates.Find(request, owner, deadline.Token);
            var visible = await cards.Read(hits, owner, deadline.Token);
            return new(
                "full",
                visible,
                await NextPlaylistPage(request, owner, hits.Length, visible.Length, deadline.Token)
            );
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            logger.LogWarning("Playlist search failed ({ErrorType})", error.GetType().Name);
            return new("unavailable", [], null);
        }
    }

    private async Task<int?> NextPlaylistPage(
        MergedSearchRequest request,
        UserId? viewer,
        int candidatesOnPage,
        int visibleOnPage,
        CancellationToken cancellationToken
    )
    {
        if (candidatesOnPage < PlaylistSearchCandidates.PageSize || request.Page >= 100)
        {
            return null;
        }

        if (visibleOnPage == PlaylistSearchCandidates.PageSize)
        {
            return request.Page + 1;
        }

        // A stale hit can shorten a page. Only an authorized hit can confirm
        // continuation, since a raw index count could reveal a hidden match.
        try
        {
            for (var page = request.Page + 1; page <= 100; page++)
            {
                var hits = await candidates.Find(
                    request with
                    {
                        Page = page,
                    },
                    viewer,
                    cancellationToken
                );
                if ((await cards.Read(hits, viewer, cancellationToken)).Length > 0)
                {
                    // Keep the next page number independent of skipped hidden matches.
                    return request.Page + 1;
                }
                if (hits.Length < PlaylistSearchCandidates.PageSize)
                {
                    break;
                }
            }
        }
        catch (Exception error)
            when (Unavailable(error, cancellationToken) || error is OperationCanceledException)
        {
            // Keep the already authorized page when lookahead fails.
            logger.LogWarning("Playlist pagination failed ({ErrorType})", error.GetType().Name);
        }

        return null;
    }

    private static bool Unavailable(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && error
            is HttpRequestException
                or IOException
                or OperationCanceledException
                or JsonException
                or NpgsqlException
                or TimeoutException
                or KeyNotFoundException;

    private sealed record CatalogResults(string Availability, JsonElement? Body);

    private sealed record PlaylistResults(
        string Availability,
        PlaylistSearchCard[] Cards,
        int? NextPage
    );
}

internal sealed record MergedSearchResponse(
    string Q,
    string? RequestId,
    string CatalogAvailability,
    string PlaylistAvailability,
    JsonElement? Catalog,
    PlaylistSearchCard[] Playlists,
    int Page,
    int? PlaylistNextPage
);

internal sealed class CatalogSearchClient(HttpClient client)
{
    public async Task<JsonElement> Get(
        MergedSearchRequest request,
        CancellationToken cancellationToken
    )
    {
        using var result = await client.GetFromJsonAsync<JsonDocument>(
            request.CatalogPath(),
            cancellationToken
        );
        return result!.RootElement.Clone();
    }
}
