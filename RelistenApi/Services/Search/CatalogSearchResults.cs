using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Relisten.Catalog;
using Relisten.Controllers.V4;
using Relisten.Identifiers;

namespace Relisten.Search;

internal static class CatalogSearchResults
{
    public static IReadOnlyDictionary<string, SearchResult> Attach(
        IReadOnlyList<RankedCandidate> candidates,
        IReadOnlyDictionary<string, SearchDisplayResources> resources
    )
    {
        var results = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = candidate.Row.Key;
            if (
                !results.ContainsKey(key)
                && resources.TryGetValue(key, out var display)
                && TryProject(candidate, display) is { } result
            )
            {
                results.Add(key, result);
            }
        }
        return results;
    }

    private static SearchResult? TryProject(
        RankedCandidate candidate,
        SearchDisplayResources display
    )
    {
        try
        {
            return Project(candidate, display);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static SearchResult? Project(RankedCandidate candidate, SearchDisplayResources display)
    {
        var result = candidate.Row switch
        {
            SearchDocument catalog => ProjectCatalog(catalog, display),
            SearchPassage passage => ProjectPassage(passage, display),
            _ => null,
        };
        if (result == null)
        {
            return null;
        }

        var highlights = candidate.Row is SearchDocument document
            ? ResourceHighlights(result, document)
            : null;
        return result with
        {
            Match =
                candidate.ReadingId == null && highlights is not { Count: > 0 }
                    ? null
                    : new(candidate.ReadingId, highlights is { Count: > 0 } ? highlights : null),
            Popularity = display.Popularity,
        };
    }

    private static Dictionary<string, string> ResourceHighlights(
        SearchResult result,
        SearchDocument document
    )
    {
        var highlights = new Dictionary<string, string>(StringComparer.Ordinal);
        var snippets = document.Highlights;
        var source = result switch
        {
            ShowSearchResult show => show.Source,
            RecordingSearchResult recording => recording.Source,
            _ => null,
        };
        if (source.HasValue)
        {
            Add(
                source.Value,
                "source",
                snippets?.GetValueOrDefault("notes"),
                ["description", "taper_notes", "lineage", "source"]
            );
            Add(
                source.Value,
                "source",
                snippets?.GetValueOrDefault("credits"),
                ["taper", "transferrer"]
            );
        }
        if (result is ReviewSearchResult review)
        {
            Add(
                review.Review,
                "review",
                snippets?.GetValueOrDefault("review_text") ?? document.ReviewExcerpt,
                ["title", "review"]
            );
        }
        return highlights;

        void Add(JsonElement resource, string prefix, string? snippet, string[] fields)
        {
            var text = SearchText.Fold(SearchText.Clean(snippet ?? ""));
            if (text.Length == 0)
            {
                return;
            }

            foreach (var field in fields)
            {
                if (
                    resource.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && SearchText
                        .Fold(SearchText.Clean(value.GetString() ?? ""))
                        .Contains(text, StringComparison.Ordinal)
                )
                {
                    highlights[$"{prefix}.{field}"] = snippet!;
                }
            }
        }
    }

    private static SearchResult? ProjectCatalog(
        SearchDocument row,
        SearchDisplayResources display
    ) =>
        (row.Kind, display.Item, display.Artist, display.Show, display.Source) switch
        {
            ("artist", { } artist, _, _, _) => new ArtistSearchResult(artist)
            {
                Key = Key("artist", artist),
            },
            ("song", { } song, { } artist, _, _) => new SongSearchResult(artist, song)
            {
                Key = Key("song", song),
            },
            ("venue", { } venue, var artist, _, _) => new VenueSearchResult(
                venue,
                Id(venue).StartsWith("imported_") ? artist : null
            )
            {
                Key = Key("venue", venue),
            },
            ("show", { } show, { } artist, _, var source)
                when row.SourceId.Length == 0 || source.HasValue => new ShowSearchResult(
                artist,
                show,
                source
            )
            {
                Key = Key("show", show),
            },
            ("performance", _, { } artist, { } show, _) when display.PreviewTrack is { } track =>
                ProjectPerformance(artist, show, track, display),
            ("recording", { } source, { } artist, { } show, _) => new RecordingSearchResult(
                artist,
                show,
                source
            )
            {
                Key = Key("recording", source),
            },
            ("review", { } review, { } artist, { } show, { } source) => new ReviewSearchResult(
                artist,
                show,
                source,
                review
            )
            {
                Key = Key("review", review),
            },
            ("tour", { } tour, { } artist, _, _) => new TourSearchResult(artist, tour)
            {
                Key = Key("tour", tour),
            },
            _ => null,
        };

    private static PerformanceSearchResult ProjectPerformance(
        JsonElement artist,
        JsonElement show,
        JsonElement track,
        SearchDisplayResources display
    )
    {
        var showId = Id(show);
        var songs = TrackSongs(track);
        var songId = display.SongId;
        var title = display.SongTitle ?? TrackTitle(track);
        if (songs != null)
        {
            // This row represents one file. Name its matched song when known,
            // otherwise use the first song in the file.
            var song = songs.FirstOrDefault(candidate => Id(candidate) == songId);
            if (song.ValueKind != JsonValueKind.Object)
            {
                song = songs[0];
            }
            songId = Id(song);
            title = song.GetProperty("name").GetString()!;
        }
        if (songId is not { Length: > 0 } || !ShowId.TryParse(showId, out var parsed))
        {
            return new(artist, show, null, track) { Key = $"performance:track:{Id(track)}" };
        }

        var summary = new CatalogPerformanceSummary(parsed, songId, title);
        using var json = JsonDocument.Parse(CatalogJsonResult.Serialize(summary));
        return new(artist, show, json.RootElement.Clone(), track)
        {
            Key =
                songs == null ? $"performance:{showId}:{songId}" : $"performance:track:{Id(track)}",
            Songs = songs,
        };
    }

    private static JsonElement[]? TrackSongs(JsonElement track)
    {
        var songs = TrackParts(track)
            .Where(part =>
                part.TryGetProperty("song", out var song) && song.ValueKind == JsonValueKind.Object
            )
            .Select(part => part.GetProperty("song"))
            .DistinctBy(Id, StringComparer.Ordinal)
            .ToArray();
        return songs.Length > 1 ? songs : null;
    }

    private static IEnumerable<JsonElement> TrackParts(JsonElement track) =>
        track.TryGetProperty("normalization", out var normalization)
        && normalization.ValueKind == JsonValueKind.Object
        && normalization.TryGetProperty("parts", out var parts)
        && parts.ValueKind == JsonValueKind.Array
            ? parts.EnumerateArray()
            : [];

    private static PassageSearchResult? ProjectPassage(
        SearchPassage row,
        SearchDisplayResources display
    )
    {
        if (
            display is not { Artist: { } artist, Show: { } show }
            || display.Tracks.Count != row.TrackIds.Length
        )
        {
            return null;
        }

        var tracks = display.Tracks.ToDictionary(Id, StringComparer.Ordinal);
        var parts = new List<SearchPassagePartResource>(row.Parts.Length);
        foreach (var part in row.Parts)
        {
            var trackPart = TrackParts(tracks[part.TrackId])
                .FirstOrDefault(value =>
                    value.TryGetProperty("ordinal", out var ordinal)
                    && ordinal.GetInt32() == part.Part
                );
            if (
                trackPart.ValueKind != JsonValueKind.Object
                || !trackPart.TryGetProperty("song", out var song)
                || song.ValueKind != JsonValueKind.Object
                || Id(song) != part.SongId
            )
            {
                return null;
            }
            parts.Add(new(song, part.TrackId, part.Part));
        }
        return new(artist, show, new(row.SourceId, display.Tracks, parts)) { Key = row.Key };
    }

    private static string TrackTitle(JsonElement track) =>
        track.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString()!
            : throw new InvalidDataException("A search preview track had no title.");

    private static string Key(string kind, JsonElement resource) => $"{kind}:{Id(resource)}";

    private static string Id(JsonElement resource) =>
        resource.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : throw new InvalidDataException("A v4 search resource had no public ID.");
}
