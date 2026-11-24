using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Search;

/// <summary>Search rows attach the existing v4 resource JSON to a kind-specific payload.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ArtistSearchResult), "artist")]
[JsonDerivedType(typeof(SongSearchResult), "song")]
[JsonDerivedType(typeof(VenueSearchResult), "venue")]
[JsonDerivedType(typeof(ShowSearchResult), "show")]
[JsonDerivedType(typeof(PerformanceSearchResult), "performance")]
[JsonDerivedType(typeof(RecordingSearchResult), "recording")]
[JsonDerivedType(typeof(ReviewSearchResult), "review")]
[JsonDerivedType(typeof(TourSearchResult), "tour")]
[JsonDerivedType(typeof(PassageSearchResult), "sequence")]
[JsonDerivedType(typeof(PlaylistSearchResult), "playlist")]
public abstract record SearchResult
{
    public required string Key { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchResultMatch? Match { get; init; }

    public JsonElement? Popularity { get; init; }
}

public sealed record SearchResultMatch(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reading,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, string>? Highlights
);

public sealed record ArtistSearchResult(JsonElement Artist) : SearchResult;

public sealed record SongSearchResult(JsonElement Artist, JsonElement Song) : SearchResult;

public sealed record VenueSearchResult(
    JsonElement Venue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Artist
) : SearchResult;

public sealed record ShowSearchResult(
    JsonElement Artist,
    JsonElement Show,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Source
) : SearchResult;

public sealed record PerformanceSearchResult(
    JsonElement Artist,
    JsonElement Show,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Performance,
    JsonElement PreviewTrack
) : SearchResult
{
    /// <summary>Ordered song resources when the preview file contains several songs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement[]? Songs { get; init; }
}

public sealed record RecordingSearchResult(JsonElement Artist, JsonElement Show, JsonElement Source)
    : SearchResult;

public sealed record ReviewSearchResult(
    JsonElement Artist,
    JsonElement Show,
    JsonElement Source,
    JsonElement Review
) : SearchResult;

public sealed record TourSearchResult(JsonElement Artist, JsonElement Tour) : SearchResult;

public sealed record PassageSearchResult(
    JsonElement Artist,
    JsonElement Show,
    SearchPassageResource Passage
) : SearchResult;

public sealed record SearchPassageResource(
    string SourceId,
    IReadOnlyList<JsonElement> Tracks,
    IReadOnlyList<SearchPassagePartResource> Parts
);

public sealed record SearchPassagePartResource(JsonElement Song, string TrackId, int Ordinal);

public sealed record PlaylistSearchResult(PlaylistSearchCardResource Playlist) : SearchResult;

public sealed record PlaylistSearchCardResource(
    string Id,
    string Name,
    int TrackCount,
    bool Owned,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Curator
);
