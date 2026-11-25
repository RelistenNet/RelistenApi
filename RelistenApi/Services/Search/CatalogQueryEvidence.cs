using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Relisten.Services.Popularity;

namespace Relisten.Search;

/// <summary>Query evidence from Postgres and the active Typesense catalog generation.</summary>
internal sealed class CatalogQueryEvidence(
    string? collection,
    SearchVocabulary vocabulary,
    SearchPassages passages,
    FrozenDictionary<string, long> songPlays,
    FrozenSet<string> reviewArtists,
    FrozenDictionary<(string Artist, string Title), long>? titlePlays = null
)
{
    public static readonly CatalogQueryEvidence Empty = new(
        null,
        SearchVocabulary.Empty,
        SearchPassages.Empty,
        FrozenDictionary<string, long>.Empty,
        FrozenSet<string>.Empty
    );

    public string? Collection { get; } = collection;
    public SearchVocabulary Vocabulary { get; } = vocabulary;
    public SearchPassages Passages { get; } = passages;
    private readonly FrozenDictionary<string, SearchDocument[]> _popularSongs = vocabulary
        .Songs.Select(song => (Song: song, Plays: songPlays.GetValueOrDefault(song.Id)))
        .Where(song => song.Plays > 0)
        .GroupBy(song => song.Song.ArtistSlug)
        .ToFrozenDictionary(
            group => group.Key,
            group =>
                group
                    .OrderByDescending(song => song.Plays)
                    .ThenBy(song => song.Song.Id, System.StringComparer.Ordinal)
                    .Take(4)
                    .Select(song => song.Song)
                    .ToArray()
        );

    public IReadOnlyList<SearchDocument> PopularSongs(string artist) =>
        _popularSongs.GetValueOrDefault(artist) ?? [];

    public long SongPlays(string songId) => songPlays.GetValueOrDefault(songId);

    public long TitlePlays(string artistSlug, string title)
    {
        var key = PopularityPerformanceKey.StoredRawTitle(title);
        var raw = titlePlays?.GetValueOrDefault((artistSlug, key)) ?? 0;
        var canonical = Vocabulary.FindSong(artistSlug, title);
        return raw + (canonical == null ? 0 : SongPlays(canonical.Id));
    }

    public bool HasReviews(string? artistSlug = null) =>
        artistSlug is null ? reviewArtists.Count > 0 : reviewArtists.Contains(artistSlug);
}
