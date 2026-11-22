using System.Collections.Frozen;
using System.Collections.Generic;

namespace Relisten.Search;

/// <summary>PostgreSQL query evidence published for one Typesense entity collection.</summary>
internal sealed class CatalogQueryEvidence(
    string? collection,
    SearchVocabulary vocabulary,
    SearchPassages passages,
    FrozenDictionary<string, long> songPlays,
    FrozenSet<string> reviewArtists
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

    public bool HasReviews(string? artistSlug = null) =>
        artistSlug is null ? reviewArtists.Count > 0 : reviewArtists.Contains(artistSlug);
}
