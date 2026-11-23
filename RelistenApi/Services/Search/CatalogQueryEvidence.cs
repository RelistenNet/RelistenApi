using System.Collections.Frozen;
using System.Collections.Generic;

namespace Relisten.Search;

/// <summary>PostgreSQL query evidence published for one Typesense entity collection.</summary>
internal sealed class CatalogQueryEvidence(
    string? collection,
    SearchVocabulary vocabulary,
    SearchPassages passages,
    FrozenDictionary<string, long> songPlays,
    FrozenSet<string> reviewArtists,
    FrozenDictionary<(string Artist, string Title), long>? titlePlays = null,
    FrozenDictionary<string, long>? soundcheckOnlyPlays = null
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

    public long SongPlays(string songId) => songPlays.GetValueOrDefault(songId);

    public long ConcertSongPlays(string songId) =>
        SongPlays(songId) - (soundcheckOnlyPlays?.GetValueOrDefault(songId) ?? 0);

    public long TitlePlays(string artistSlug, string title)
    {
        var raw = titlePlays?.GetValueOrDefault((artistSlug, title)) ?? 0;
        var canonical = Vocabulary.FindSong(artistSlug, title);
        return raw + (canonical == null ? 0 : ConcertSongPlays(canonical.Id));
    }

    public bool HasReviews(string? artistSlug = null) =>
        artistSlug is null ? reviewArtists.Count > 0 : reviewArtists.Contains(artistSlug);
}
