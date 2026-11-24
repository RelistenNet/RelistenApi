using System.Text.Json;
using Npgsql;
using Relisten.Search;

namespace RelistenUserService.Search;

internal sealed class MergedSearch(
    CatalogSearchClient catalog,
    PlaylistSearchCandidates candidates,
    PlaylistSearchCards cards,
    ILogger<MergedSearch> logger
)
{
    public async Task<SearchResponse> Run(
        MergedSearchRequest request,
        Func<CancellationToken, Task<UserId?>>? viewer,
        CancellationToken cancellationToken
    )
    {
        var catalogTask = request.WantsCatalog
            ? SearchCatalog(request, cancellationToken)
            : Task.FromResult(new CatalogResults("not_requested", null));

        // Unconstrained queries can run both halves in parallel. A selected
        // reading needs the catalog's validated artist or place before retrieval.
        var selectReading =
            request.WantsPlaylists
            && request.WantsCatalog
            && request.ReadingId is not (null or "query" or "literal");
        var selected = selectReading ? await catalogTask : null;
        // A failed exchange cannot validate a selected artist or place. With
        // no selected reading, the playlist half can still run independently.
        var playlistTask =
            selectReading && (selected?.Body == null || selected.Availability == "unavailable")
                ? Task.FromResult(new PlaylistResults("unavailable", [], null, []))
                : RequestPlaylists(request, selected?.Body?.Constraints, viewer, cancellationToken);
        await Task.WhenAll(catalogTask, playlistTask);

        return ComposePage(request, await catalogTask, await playlistTask);
    }

    private Task<PlaylistResults> RequestPlaylists(
        MergedSearchRequest request,
        SearchConstraints? constraints,
        Func<CancellationToken, Task<UserId?>>? viewer,
        CancellationToken token
    )
    {
        if (!request.WantsPlaylists || ExcludesPlaylists(constraints))
        {
            return Task.FromResult(new PlaylistResults("not_requested", [], null, []));
        }

        return SearchPlaylists(
            request with
            {
                ArtistId = constraints?.ArtistId ?? request.ArtistId,
            },
            viewer,
            request.IsAll,
            token
        );
    }

    // A full date or a selected place identifies catalog events. Playlists
    // have no event date or venue, so the rule also applies to prefetched results.
    private static bool ExcludesPlaylists(SearchConstraints? constraints) =>
        constraints is { PlaceSelected: true } or { Date: not null };

    private static SearchResponse ComposePage(
        MergedSearchRequest request,
        CatalogResults catalog,
        PlaylistResults playlists
    )
    {
        var body = catalog.Body;
        var playlistAvailability = ExcludesPlaylists(body?.Constraints)
            ? "not_requested"
            : playlists.Availability;
        var catalogItems = body?.Candidates ?? [];
        var playlistItems =
            playlistAvailability == "not_requested" ? [] : PlaylistCandidates(request, playlists);
        var results = MergeRows(catalogItems, playlistItems, body?.PageLimit ?? 24, request.IsAll);
        var lowerBounds = body?.Page.CountLowerBounds is { } catalogBounds
            ? new Dictionary<string, int>(catalogBounds)
            : new Dictionary<string, int>();
        if (playlistAvailability is "full" or "partial")
        {
            // Only indexed hits checked against current library permissions
            // count toward the tab. The live exact-title fallback is All-only.
            lowerBounds["playlists"] = playlists.Cards.Length;
        }

        return new(
            request.Q ?? "",
            request.RequestId,
            request.Scope.ToLowerInvariant(),
            new(catalog.Availability, playlistAvailability),
            body?.Page.Counts ?? new Dictionary<string, int>(),
            body?.Page.Readings ?? SearchReadings.Empty,
            request.IsAll ? null : body?.Page.NextPage ?? playlists.NextPage,
            body?.Page.MoreScopes ?? [],
            results
        )
        {
            CountLowerBounds = lowerBounds,
        };
    }

    private static SearchResult[] MergeRows(
        SearchCandidate[] catalog,
        SearchCandidate[] playlists,
        int limit,
        bool all
    )
    {
        if (!all)
        {
            return catalog
                .Concat(playlists.OrderByDescending(candidate => candidate.Order))
                .Select(candidate => candidate.Result)
                .ToArray();
        }

        // The catalog has already applied its grouping and playback rules.
        // Insert playlists by shared scores without changing catalog order.
        // Reviews are supporting evidence, so playlist results appear above them.
        var remaining = new Queue<SearchCandidate>(
            playlists
                .OrderByDescending(candidate => candidate.Order)
                .ThenBy(candidate => candidate.Result.Key, StringComparer.Ordinal)
        );
        var result = new List<SearchResult>();
        foreach (var candidate in catalog)
        {
            while (
                remaining.TryPeek(out var playlist)
                && (
                    candidate.Result is ReviewSearchResult
                    || playlist.Order.CompareTo(candidate.Order) > 0
                )
            )
            {
                result.Add(remaining.Dequeue().Result);
            }
            result.Add(candidate.Result);
        }

        return result.Concat(remaining.Select(candidate => candidate.Result)).Take(limit).ToArray();
    }

    private sealed record PlaylistRankItem(string Key) : ISearchRankItem
    {
        public string Kind => "playlist";
    }

    private static SearchCandidate[] PlaylistCandidates(
        MergedSearchRequest request,
        PlaylistResults playlists
    )
    {
        var title = request.Q?.Trim().Normalize();
        var indexed = playlists.Cards.DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        return playlists
            .ExactLibrary.Select(card =>
                card with
                {
                    PopularityPercentile =
                        indexed.GetValueOrDefault(card.Id)?.PopularityPercentile ?? 0,
                }
            )
            .Concat(playlists.Cards)
            .DistinctBy(card => card.Id)
            .Select(card =>
            {
                var match = MatchEvidence.Read(
                    request.Q ?? "",
                    ("title", card.Label, 1),
                    ("description", card.Description, .45),
                    ("curator", card.Curator, .35)
                );
                if (card.SupportingMatch is { } support && support.Relevance > match.Relevance)
                {
                    match = match with
                    {
                        Relevance = support.Relevance,
                        Fields = [.. match.Fields, .. support.Fields],
                    };
                }
                var library =
                    (card.Owned || card.Followed)
                    && string.Equals(
                        title,
                        card.Label.Trim().Normalize(),
                        StringComparison.OrdinalIgnoreCase
                    );
                if (library)
                {
                    match = new(MatchTier.Exact, ["title"], 1);
                }
                var priority =
                    !library ? 0
                    : card.Owned ? RankedCandidate.OwnedPlaylistTitle
                    : RankedCandidate.FollowedPlaylistTitle;
                var key = $"playlist:{card.Id.Value}";
                var result = new PlaylistSearchResult(
                    new(
                        card.Id.Value,
                        card.Label,
                        card.TrackCount,
                        card.Owned,
                        card.Description,
                        card.PublicCode,
                        card.Curator
                    )
                )
                {
                    Key = key,
                };
                var rank = new RankedCandidate(
                    new PlaylistRankItem(key),
                    match,
                    priority,
                    card.PopularityPercentile,
                    Affinity: card.Owned || card.Followed ? 1 : 0
                );
                return new SearchCandidate(result, rank.MergeOrder());
            })
            .ToArray();
    }

    private async Task<CatalogResults> SearchCatalog(
        MergedSearchRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await catalog.Get(request, cancellationToken);
            return new(result.Page.Availability.Catalog, result);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Query text and index result bodies can contain private listener input.
            logger.LogWarning("Catalog search failed ({ErrorType})", error.GetType().Name);
            return new("unavailable", null);
        }
    }

    private async Task<PlaylistResults> SearchPlaylists(
        MergedSearchRequest request,
        Func<CancellationToken, Task<UserId?>>? viewer,
        bool mixed,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));

        UserId? owner;
        try
        {
            owner = viewer is null ? null : await viewer(deadline.Token);
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            logger.LogWarning(
                "Playlist search viewer lookup failed ({ErrorType})",
                error.GetType().Name
            );
            return new("unavailable", [], null, []);
        }

        var indexed = await SearchIndexedPlaylists(
            request,
            owner,
            deadline.Token,
            cancellationToken
        );
        // The database fallback has time to run even if the index spent its budget.
        using var libraryDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        libraryDeadline.CancelAfter(TimeSpan.FromSeconds(1));
        return mixed && owner.HasValue
            ? await AddExactLibrary(
                request,
                owner.Value,
                indexed,
                libraryDeadline.Token,
                cancellationToken
            )
            : indexed;
    }

    private async Task<PlaylistResults> SearchIndexedPlaylists(
        MergedSearchRequest request,
        UserId? owner,
        CancellationToken deadline,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var hits = await candidates.Find(request, owner, deadline);
            var visible = await cards.Read(hits, owner, deadline);
            var nextPage = request.IsAll
                ? null
                : await NextPlaylistPage(
                    request,
                    owner,
                    hits.Length,
                    visible.Length,
                    deadline,
                    cancellationToken
                );
            return new("full", visible, nextPage, []);
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            logger.LogWarning("Playlist search failed ({ErrorType})", error.GetType().Name);
            return new("unavailable", [], null, []);
        }
    }

    private async Task<PlaylistResults> AddExactLibrary(
        MergedSearchRequest request,
        UserId owner,
        PlaylistResults indexed,
        CancellationToken deadline,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var exact = await cards.ExactLibraryTitle(request, owner, deadline);
            return exact.Length == 0
                ? indexed
                : indexed with
                {
                    Availability = indexed.Availability == "full" ? "full" : "partial",
                    ExactLibrary = exact,
                };
        }
        catch (Exception error) when (Unavailable(error, cancellationToken))
        {
            logger.LogWarning("Library title lookup failed ({ErrorType})", error.GetType().Name);
            return indexed.Availability == "full"
                ? indexed with
                {
                    Availability = "partial",
                }
                : indexed;
        }
    }

    private async Task<int?> NextPlaylistPage(
        MergedSearchRequest request,
        UserId? viewer,
        int candidatesOnPage,
        int visibleOnPage,
        CancellationToken playlistCancellation,
        CancellationToken requestCancellation
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

        try
        {
            return await FindVisibleContinuation(request, viewer, playlistCancellation);
        }
        catch (Exception error) when (Unavailable(error, requestCancellation))
        {
            // Keep the already authorized page when lookahead fails.
            logger.LogWarning("Playlist pagination failed ({ErrorType})", error.GetType().Name);
            return null;
        }
    }

    private async Task<int?> FindVisibleContinuation(
        MergedSearchRequest request,
        UserId? viewer,
        CancellationToken cancellationToken
    )
    {
        // Use only authorized hits to confirm continuation after a short page.
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

    private sealed record CatalogResults(string Availability, CatalogSearchEnvelope? Body);

    private sealed record PlaylistResults(
        string Availability,
        PlaylistSearchCard[] Cards,
        int? NextPage,
        PlaylistSearchCard[] ExactLibrary
    );
}

internal sealed class CatalogSearchClient(HttpClient client)
{
    public async Task<CatalogSearchEnvelope> Get(
        MergedSearchRequest request,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(
            request.Typing ? TimeSpan.FromMilliseconds(3500) : TimeSpan.FromSeconds(6)
        );
        return await client.GetFromJsonAsync<CatalogSearchEnvelope>(
                request.CatalogPath(),
                SearchJson.Options,
                deadline.Token
            ) ?? throw new JsonException("Catalog search returned no response.");
    }
}
