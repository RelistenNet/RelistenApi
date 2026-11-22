using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal sealed class SearchVocabulary
{
    private readonly FrozenDictionary<string, Match[]> _terms;
    private readonly string[] _keys;
    private readonly FrozenDictionary<(char First, int Length, int Spaces), string[]> _spellings;
    private readonly FrozenDictionary<string, FrozenSet<string>> _artistTerms;
    private readonly FrozenDictionary<string, FrozenSet<string>> _placeArtists;
    private readonly FrozenDictionary<string, SearchDocument[]> _placeVenues;
    private readonly int _maxPlaceWords;
    private readonly FrozenDictionary<(string Artist, string Title), SearchDocument> _songs;
    private readonly FrozenDictionary<(string Artist, string Title), SearchDocument> _tours;
    private readonly FrozenDictionary<(string Artist, string Key), string?> _compactTitles;
    private readonly FrozenDictionary<string, SearchDocument[]> _curatedSongs;

    public static readonly SearchVocabulary Empty = new([], []);

    public FrozenDictionary<string, string> ArtistNames { get; }
    public FrozenDictionary<string, string> ArtistSlugsById { get; }
    public FrozenDictionary<string, SearchDocument> ArtistDocuments { get; }
    public FrozenSet<string> TourNames { get; }

    public SearchVocabulary(
        IEnumerable<(SearchDocument Document, string[] Aliases)> entries,
        IEnumerable<SearchDocument> tours
    )
    {
        var terms = new Dictionary<string, List<Match>>();
        var artistNames = new Dictionary<string, string>();
        var artistDocuments = new Dictionary<string, SearchDocument>();
        var artistTerms = new Dictionary<string, HashSet<string>>();
        var placeVenues = new Dictionary<string, HashSet<SearchDocument>>();
        var songs = new Dictionary<(string, string), SearchDocument>();
        foreach (var (document, aliases) in entries)
        {
            var names = aliases
                .Prepend(document.Name)
                .Where(name => name.Length > 0)
                .Distinct()
                .ToArray();
            foreach (var name in names)
            {
                Add(terms, name, new Match(document, name == document.Name));
            }

            switch (document.Kind)
            {
                case "artist":
                    artistNames[document.Name] = document.ArtistSlug;
                    artistDocuments[document.ArtistSlug] = document;
                    IndexArtistTerms(artistTerms, document, names);
                    break;
                case "song":
                    songs[(document.ArtistSlug, document.Name)] = document;
                    break;
                case "venue":
                    foreach (
                        var name in names
                            .Append(SearchText.Fold(document.CityLabel))
                            .Where(name => name.Length > 0)
                    )
                    {
                        Add(placeVenues, name, document);
                    }
                    break;
            }
        }

        ArtistNames = artistNames.ToFrozenDictionary();
        ArtistDocuments = artistDocuments.ToFrozenDictionary();
        ArtistSlugsById = artistDocuments.Values.ToFrozenDictionary(
            doc => doc.Id,
            doc => doc.ArtistSlug
        );
        _terms = terms.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        _keys = terms.Keys.Order(StringComparer.Ordinal).ToArray();
        _spellings = _keys
            .GroupBy(SpellingKey)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray());
        _artistTerms = Freeze(artistTerms);
        _placeArtists = AddPlaceAliases(placeVenues);
        _placeVenues = placeVenues.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray()
        );
        _maxPlaceWords = _placeArtists.Keys.Select(WordCount).DefaultIfEmpty(0).Max();
        _songs = songs.ToFrozenDictionary();
        _tours = tours
            .GroupBy(tour => (tour.ArtistSlug, tour.Name))
            .ToFrozenDictionary(group => group.Key, group => group.Last());
        TourNames = _tours.Keys.Select(key => key.Title).ToFrozenSet();
        _compactTitles = songs
            .Keys.Select(pair => (Artist: pair.Item1, Title: pair.Item2, Key: TitleKey(pair.Item2)))
            .Where(row => row.Key.Length > 0)
            .GroupBy(row => (row.Artist, row.Key))
            .ToFrozenDictionary(
                group => group.Key,
                group => group.Count() == 1 ? group.First().Title : null
            );
        _curatedSongs = SearchVocabularyRules
            .SongAliases.Select(rule => (rule.Alias, Document: FindSong(rule.Artist, rule.Title)))
            .Where(row => row.Document is not null)
            .GroupBy(row => row.Alias)
            .ToFrozenDictionary(
                group => group.Key,
                group => group.Select(row => row.Document!).ToArray()
            );
    }

    private static void IndexArtistTerms(
        Dictionary<string, HashSet<string>> artistTerms,
        SearchDocument artist,
        string[] names
    )
    {
        if (artist.Popularity > 0)
        {
            foreach (var name in names)
            {
                Add(artistTerms, name, artist.ArtistSlug);
            }
        }

        if (artist.Popularity >= 10)
        {
            Add(artistTerms, SearchText.Fold(artist.ArtistSlug), artist.ArtistSlug);
        }
    }

    private static FrozenDictionary<string, FrozenSet<string>> AddPlaceAliases(
        Dictionary<string, HashSet<SearchDocument>> placeVenues
    )
    {
        var placeArtists = placeVenues.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.SelectMany(venue => venue.ArtistSlugs).ToHashSet()
        );
        foreach (var (artist, place, alias) in SearchVocabularyRules.PlaceAliases)
        {
            Add(placeArtists, alias, artist);
            if (placeVenues.TryGetValue(place, out var venues))
            {
                foreach (var venue in venues.Where(venue => venue.ArtistSlugs.Contains(artist)))
                {
                    Add(placeVenues, alias, venue);
                }
            }
        }

        return Freeze(placeArtists);
    }

    public SearchDocument? FindSong(string? artist, string foldedTitle) =>
        artist is null ? null : _songs.GetValueOrDefault((artist, foldedTitle));

    public SearchDocument? FindTour(string artist, string foldedName) =>
        _tours.GetValueOrDefault((artist, foldedName));

    public IReadOnlyList<SearchDocument> Lookup(
        string terms,
        string? artist = null,
        bool curated = false,
        bool fuzzy = false,
        int minLength = 2,
        int limit = 6,
        string? kind = null
    )
    {
        if (terms.Length == 0 || terms.Length < minLength)
        {
            return [];
        }

        var hits = new Dictionary<string, SearchDocument>();
        void Remember(SearchDocument document, double quality, string match)
        {
            if (kind is not null && document.Kind != kind)
            {
                return;
            }

            if (
                !string.IsNullOrEmpty(artist)
                && document.ArtistSlug != artist
                && !document.ArtistSlugs.Contains(artist)
            )
            {
                return;
            }

            if (!hits.TryGetValue(document.Id, out var old) || old.CatalogQuality < quality)
            {
                hits[document.Id] = document with
                {
                    CatalogQuality = quality,
                    CatalogMatch = match,
                };
            }
        }

        var start = Array.BinarySearch(_keys, terms, StringComparer.Ordinal);
        for (var index = start < 0 ? ~start : start; index < _keys.Length; index++)
        {
            var key = _keys[index];
            if (!key.StartsWith(terms, StringComparison.Ordinal))
            {
                break;
            }

            foreach (var hit in _terms[key])
            {
                if (key == terms)
                {
                    Remember(hit.Document, hit.Canonical ? 6 : 5, hit.Canonical ? "name" : "alias");
                }
                else
                {
                    Remember(hit.Document, hit.Canonical ? 3 : 2, "prefix");
                }
            }
        }

        if (curated && _curatedSongs.TryGetValue(terms, out var aliases))
        {
            foreach (var document in aliases)
            {
                Remember(document, 5, "curated_alias");
            }
        }

        if (fuzzy && terms.Length >= 4 && !hits.Values.Any(hit => hit.CatalogQuality >= 5))
        {
            foreach (var key in SimilarTerms(terms))
            {
                foreach (var hit in _terms[key])
                {
                    Remember(hit.Document, hit.Canonical ? 4 : 3.5, "spelling");
                }
            }
        }

        var ranked = hits
            .Values.OrderByDescending(hit => hit.CatalogQuality)
            .ThenByDescending(hit => KindRank(hit.Kind))
            .ThenByDescending(hit => hit.Popularity)
            .ThenByDescending(hit => hit.Id, StringComparer.Ordinal)
            .ToList();
        if (string.IsNullOrEmpty(artist) && !terms.Contains(' '))
        {
            var alternate = ranked.FindIndex(hit => hit.Kind == "artist");
            if (alternate >= 4)
            {
                var document = ranked[alternate];
                ranked.RemoveAt(alternate);
                ranked.Insert(3, document);
            }
        }

        return ranked.Take(limit).ToArray();
    }

    public (string? Artist, string? Kind) FullArtist(string terms, string? scope)
    {
        if (_artistTerms.TryGetValue(terms, out var slugs))
        {
            var best = slugs
                .Where(slug => string.IsNullOrEmpty(scope) || slug == scope)
                .OrderByDescending(slug => ArtistDocuments[slug].Popularity)
                .ThenByDescending(slug => slug, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is not null)
            {
                return (best, "artist_identity");
            }
        }

        var finalSpace = terms.LastIndexOf(' ');
        if (finalSpace < 0 || terms.Length - finalSpace <= 4)
        {
            return (null, null);
        }

        var prefixes = Lookup(terms, scope, limit: 40)
            .Where(doc =>
                doc.Kind == "artist"
                && doc.Popularity >= 20
                && doc.Name.StartsWith(terms, StringComparison.Ordinal)
            )
            .Take(2)
            .ToArray();
        return prefixes.Length == 1 ? (prefixes[0].ArtistSlug, "artist_name_prefix") : (null, null);
    }

    public IEnumerable<(
        int Quality,
        string Artist,
        string Remainder,
        string Evidence
    )> SupportedArtistRemainders(string terms, string? scope = null)
    {
        for (var split = terms.IndexOf(' '); split >= 0; split = terms.IndexOf(' ', split + 1))
        {
            var left = terms[..split];
            var right = terms[(split + 1)..];
            foreach (
                var reading in ReadArtistRemainder(left, right, scope)
                    .Concat(ReadArtistRemainder(right, left, scope))
            )
            {
                yield return reading;
            }
        }
    }

    private IEnumerable<(
        int Quality,
        string Artist,
        string Remainder,
        string Evidence
    )> ReadArtistRemainder(string name, string remainder, string? scope)
    {
        if (!_artistTerms.TryGetValue(name, out var artists))
        {
            yield break;
        }

        foreach (var artist in artists)
        {
            if (!string.IsNullOrEmpty(scope) && scope != artist)
            {
                continue;
            }

            if (
                _placeArtists.TryGetValue(remainder, out var placeArtists)
                && placeArtists.Contains(artist)
            )
            {
                yield return (4, artist, remainder, "venue in artist catalog");
            }
            else if (
                Lookup(remainder, artist, curated: true, limit: 4)
                    .Any(doc => doc.Kind == "song" && doc.CatalogQuality >= 3)
            )
            {
                yield return (3, artist, remainder, "song in artist catalog");
            }
        }
    }

    public string? CompactTitle(string artist, string terms)
    {
        if (FindSong(artist, terms) is not null)
        {
            return null;
        }

        var key = TitleKey(terms);
        return key.Split(' ').Any(word => word.Length >= 4)
            ? _compactTitles.GetValueOrDefault((artist, key))
            : null;
    }

    public IEnumerable<string> Places(string terms)
    {
        if (_maxPlaceWords == 0)
        {
            yield break;
        }

        var words = terms.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start < words.Length; start++)
        {
            for (var count = 1; count <= Math.Min(words.Length - start, _maxPlaceWords); count++)
            {
                var phrase = string.Join(' ', words, start, count);
                if (_placeArtists.ContainsKey(phrase))
                {
                    yield return phrase;
                }
            }
        }
    }

    public IReadOnlySet<string> RecognizeVenues(string terms)
    {
        foreach (
            var phrase in Places(terms)
                .OrderByDescending(WordCount)
                .ThenByDescending(value => value.Length)
                .ThenBy(value => value, StringComparer.Ordinal)
        )
        {
            if (!_placeVenues.TryGetValue(phrase, out var places))
            {
                continue;
            }

            var venues = places
                .Where(doc =>
                    phrase == terms
                    || phrase.Contains(' ')
                    || SearchText.Fold(doc.CityLabel) != phrase
                )
                .Select(doc => doc.VenueId)
                .ToHashSet();
            if (venues.Count > 0)
            {
                return venues;
            }
        }

        return FrozenSet<string>.Empty;
    }

    private IEnumerable<string> SimilarTerms(string terms)
    {
        var (first, length, spaces) = SpellingKey(terms);
        for (var delta = -1; delta <= 1; delta++)
        {
            if (_spellings.TryGetValue((first, length + delta, spaces), out var keys))
            {
                foreach (var key in keys)
                {
                    if (SimilarName(terms, key))
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    private static bool SimilarName(string left, string right)
    {
        if (OneEdit(left, right))
        {
            return true;
        }

        var leftWords = left.Split(' ');
        var rightWords = right.Split(' ');
        if (leftWords.Length < 2)
        {
            return false;
        }

        var different = leftWords
            .Zip(rightWords)
            .Where(pair => pair.First != pair.Second)
            .Take(2)
            .ToArray();
        if (different.Length != 1)
        {
            return false;
        }

        // A second edit is only allowed in one word if the rest of the title agrees.
        var (first, second) = different[0];
        if (Math.Min(first.Length, second.Length) < 5)
        {
            return false;
        }

        var letters = first.ToCharArray();
        for (var index = 0; index + 1 < letters.Length; index++)
        {
            if (letters[index] == letters[index + 1])
            {
                continue;
            }

            (letters[index], letters[index + 1]) = (letters[index + 1], letters[index]);
            if (OneEdit(letters, second))
            {
                return true;
            }
            (letters[index], letters[index + 1]) = (letters[index + 1], letters[index]);
        }

        return false;
    }

    private static bool OneEdit(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length > right.Length)
        {
            return OneEdit(right, left);
        }

        var difference = 0;
        while (difference < left.Length && left[difference] == right[difference])
        {
            difference++;
        }

        if (left.Length + 1 == right.Length)
        {
            return left[difference..].SequenceEqual(right[(difference + 1)..]);
        }

        if (left.Length != right.Length || difference == left.Length)
        {
            return false;
        }

        return left[(difference + 1)..].SequenceEqual(right[(difference + 1)..])
            || (
                difference + 1 < left.Length
                && left[difference] == right[difference + 1]
                && left[difference + 1] == right[difference]
                && left[(difference + 2)..].SequenceEqual(right[(difference + 2)..])
            );
    }

    private static string TitleKey(string title) =>
        string.Join(
            ' ',
            title
                .Split(' ')
                .Where(word => word is not ("a" or "an" or "and" or "of" or "the" or "i" or "am"))
        );

    private static int WordCount(string text) => 1 + text.Count(character => character == ' ');

    private static (char First, int Length, int Spaces) SpellingKey(string text) =>
        (text[0], text.Length, WordCount(text) - 1);

    private static int KindRank(string kind) =>
        kind switch
        {
            "artist" => 3,
            "song" => 2,
            "venue" => 1,
            _ => 0,
        };

    private static void Add<TKey, TCollection, TItem>(
        Dictionary<TKey, TCollection> entries,
        TKey key,
        TItem item
    )
        where TKey : notnull
        where TCollection : ICollection<TItem>, new()
    {
        if (!entries.TryGetValue(key, out var values))
        {
            values = new TCollection();
            entries.Add(key, values);
        }

        values.Add(item);
    }

    private static FrozenDictionary<string, FrozenSet<string>> Freeze(
        Dictionary<string, HashSet<string>> entries
    ) => entries.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet());

    private sealed record Match(SearchDocument Document, bool Canonical);
}
