using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Search;

internal sealed record SearchDisplayResources(
    JsonElement? Item,
    JsonElement? Artist,
    JsonElement? Show,
    JsonElement? Source,
    JsonElement? PreviewTrack,
    string? SongId,
    string? SongTitle,
    IReadOnlyList<JsonElement> Tracks,
    JsonElement? Popularity
);

/// <summary>Attaches indexed v4 resources to the ranked window with one Typesense batch.</summary>
internal sealed class CatalogSearchDisplay(TypesenseClient typesense, SearchCollections collections)
{
    private const int PageSize = 250;
    private const string IncludeFields =
        "id,kind,artist_slug,display,display_song_id,display_song_title,display_popularity";

    public async Task<IReadOnlyDictionary<string, SearchDisplayResources>> Read(
        IReadOnlyList<RankedCandidate> candidates,
        CancellationToken cancellationToken
    )
    {
        var lookups = new ResourceLookups();
        foreach (var candidate in candidates)
        {
            lookups.Add(candidate.Row);
        }

        var resources = await ReadResources(lookups, cancellationToken);
        return candidates
            .Select(candidate => candidate.Row)
            .DistinctBy(row => row.Key, StringComparer.Ordinal)
            .ToDictionary(row => row.Key, resources.ForRow, StringComparer.Ordinal);
    }

    private async Task<StoredResources> ReadResources(
        ResourceLookups lookups,
        CancellationToken cancellationToken
    )
    {
        var searches = lookups.Requests(collections).ToArray();
        var resources = new StoredResources();
        if (searches.Length == 0)
        {
            return resources;
        }

        using var response = await typesense.MultiSearch(new { searches }, cancellationToken);
        var root = response.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() != searches.Length
        )
        {
            throw new InvalidDataException(
                "Typesense returned an invalid search display response."
            );
        }

        foreach (var result in results.EnumerateArray())
        {
            SearchRequests.ThrowIfCollectionMissing(result);
            if (
                result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("hits", out var hits)
                || hits.ValueKind != JsonValueKind.Array
            )
            {
                continue;
            }

            foreach (var hit in hits.EnumerateArray())
            {
                if (
                    hit.ValueKind != JsonValueKind.Object
                    || !hit.TryGetProperty("document", out var document)
                    || document.ValueKind != JsonValueKind.Object
                )
                {
                    continue;
                }
                resources.Add(document);
            }
        }

        return resources;
    }

    private sealed class ResourceLookups
    {
        private readonly Dictionary<string, HashSet<string>> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _artistSlugs = new(StringComparer.Ordinal);

        public void Add(ISearchRankItem row)
        {
            if (row is SearchDocument catalog)
            {
                Add(Collection(catalog.Kind), catalog.Id);
                Add(SearchIndexSchema.Entities, catalog.ShowId);
                Add(SearchIndexSchema.Recordings, SourceId(catalog));
                AddArtist(catalog.ArtistSlug);
            }
            else if (row is SearchPassage passage)
            {
                AddArtist(passage.ArtistSlug);
                Add(SearchIndexSchema.Entities, passage.ShowId);
                foreach (var id in passage.TrackIds)
                {
                    Add(SearchIndexSchema.Entities, id);
                }
            }
        }

        private void AddArtist(string slug)
        {
            if (slug.Length > 0)
            {
                _artistSlugs.Add(slug);
            }
        }

        private void Add(string collection, string id)
        {
            if (id.Length == 0)
            {
                return;
            }

            if (!_ids.TryGetValue(collection, out var ids))
            {
                ids = new(StringComparer.Ordinal);
                _ids.Add(collection, ids);
            }

            ids.Add(id);
        }

        public IEnumerable<SearchRequest> Requests(SearchCollections collections)
        {
            foreach (var (collection, ids) in _ids)
            {
                foreach (var page in ids.Chunk(PageSize))
                {
                    yield return ByValues(collections, collection, "id", page);
                }
            }

            foreach (var page in _artistSlugs.Chunk(PageSize))
            {
                yield return ByValues(
                    collections,
                    SearchIndexSchema.Entities,
                    "artist_slug",
                    page
                ) with
                {
                    FilterBy = $"kind:=artist && artist_slug:=[{Join(page)}]",
                };
            }
        }

        private static SearchRequest ByValues(
            SearchCollections collections,
            string collection,
            string field,
            string[] values
        ) =>
            new(
                collections.Name(collection),
                "*",
                "name",
                IncludeFields,
                "none",
                "popularity:desc",
                values.Length
            )
            {
                FilterBy = $"{field}:=[{Join(values)}]",
            };

        // IDs are catalog-generated and slugs are identity fields, never query text.
        private static string Join(IEnumerable<string> values) =>
            string.Join(',', values.Select(value => $"`{value}`"));
    }

    private sealed class StoredResources
    {
        private readonly Dictionary<string, IndexedResource> _documents = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, JsonElement> _artists = new(StringComparer.Ordinal);

        public void Add(JsonElement document)
        {
            var id = Text(document, "id");
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            var resource = new IndexedResource(
                JsonObject(document, "display"),
                JsonObject(document, "display_popularity"),
                Text(document, "display_song_id"),
                Text(document, "display_song_title")
            );
            _documents.TryAdd(id, resource);
            if (
                Text(document, "kind") == "artist"
                && Text(document, "artist_slug") is { Length: > 0 } slug
                && resource.Value is { } artist
            )
            {
                _artists.TryAdd(slug, artist);
            }
        }

        public SearchDisplayResources ForRow(ISearchRankItem row) =>
            row switch
            {
                SearchDocument catalog => ForCatalog(catalog),
                SearchPassage passage => ForPassage(passage),
                _ => throw new ArgumentOutOfRangeException(nameof(row)),
            };

        private SearchDisplayResources ForCatalog(SearchDocument row)
        {
            var item = _documents.GetValueOrDefault(row.Id);
            return new(
                item?.Value,
                _artists.TryGetValue(row.ArtistSlug, out var artist) ? artist : null,
                Value(row.ShowId),
                Value(SourceId(row)),
                row.Kind == "performance" ? item?.Value : null,
                item?.SongId,
                item?.SongTitle,
                [],
                item?.Popularity
            );
        }

        private SearchDisplayResources ForPassage(SearchPassage row) =>
            new(
                null,
                _artists.TryGetValue(row.ArtistSlug, out var artist) ? artist : null,
                Value(row.ShowId),
                null,
                null,
                null,
                null,
                row.TrackIds.Select(Value).OfType<JsonElement>().ToArray(),
                // A selected passage is an exact tape. Per-show song totals
                // would mix its recordings and could count a medley file twice.
                null
            );

        private JsonElement? Value(string id) => _documents.GetValueOrDefault(id)?.Value;

        private static JsonElement? JsonObject(JsonElement document, string name) =>
            document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
                ? value.Clone()
                : null;

        private static string? Text(JsonElement document, string name) =>
            document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string Collection(string kind) =>
        kind switch
        {
            "performance" => SearchIndexSchema.Performances,
            "recording" => SearchIndexSchema.Recordings,
            "review" => SearchIndexSchema.Reviews,
            _ => SearchIndexSchema.Entities,
        };

    private static string SourceId(SearchDocument row) =>
        row.Kind is "show" or "review" ? row.SourceId : "";

    private sealed record IndexedResource(
        JsonElement? Value,
        JsonElement? Popularity,
        string? SongId,
        string? SongTitle
    );
}
