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
    private readonly FrozenDictionary<string, SearchDocument> _artistInitialisms;
    private readonly FrozenDictionary<string, FrozenSet<string>> _placeArtists;
    private readonly FrozenDictionary<string, SearchDocument[]> _placeVenues;
    private readonly FrozenDictionary<string, SearchDocument> _venuesById;
    private readonly FrozenDictionary<string, SearchPlaceAlias> _placeAliases;
    private readonly FrozenDictionary<string, SearchPlaceAlias> _cityPrefixes;
    private readonly ILookup<string, string> _aliasesByVenue;
    private readonly IReadOnlySet<(string VenueId, string Key)> _noUnqualifiedReading;
    private readonly FrozenDictionary<string, string[]> _placePrefixes;
    private readonly int _maxPlaceWords;
    private readonly FrozenDictionary<(string Artist, string Title), SearchDocument> _songs;
    private readonly FrozenDictionary<(string Artist, string Title), SearchDocument> _tours;
    private readonly FrozenDictionary<(string Artist, string Key), string?> _compactTitles;
    private readonly FrozenDictionary<string, SearchDocument[]> _curatedSongs;
    private readonly ILookup<string, SearchReviewedNicknames.SongTarget> _reviewedSongs;

    public static readonly SearchVocabulary Empty = new([], []);

    public FrozenDictionary<string, string> ArtistNames { get; }
    public FrozenDictionary<string, string> ArtistSlugsById { get; }
    public FrozenDictionary<string, SearchDocument> ArtistDocuments { get; }
    public FrozenSet<string> TourNames { get; }
    internal IEnumerable<SearchDocument> Songs => _songs.Values;

    public SearchVocabulary(
        IEnumerable<(SearchDocument Document, string[] Aliases)> entries,
        IEnumerable<SearchDocument> tours,
        IReadOnlySet<string>? reservedInitialismKeys = null,
        FrozenDictionary<string, SearchPlaceAlias>? placeAliases = null,
        IReadOnlySet<(string VenueId, string Key)>? noUnqualifiedReading = null,
        IReadOnlyDictionary<(long Id, string ArtistSlug), Guid>? reviewedSetlistSongs = null,
        IEnumerable<SearchReviewedNicknames.SongTarget>? indexedNicknameSongs = null
    )
    {
        _noUnqualifiedReading = noUnqualifiedReading ?? FrozenSet<(string, string)>.Empty;
        _placeAliases = placeAliases ?? FrozenDictionary<string, SearchPlaceAlias>.Empty;
        var terms = new Dictionary<string, List<Match>>();
        var artistNames = new Dictionary<string, string>();
        var artistDocuments = new Dictionary<string, SearchDocument>();
        var artistTerms = new Dictionary<string, HashSet<string>>();
        var placeVenues = new Dictionary<string, HashSet<SearchDocument>>();
        var venuesById = new Dictionary<string, SearchDocument>();
        var songs = new Dictionary<(string, string), SearchDocument>();
        var catalogSongs = new List<SearchReviewedNicknames.CatalogSong>();
        foreach (var (document, aliases) in entries)
        {
            var names = aliases
                .Prepend(document.Name)
                .Where(name => name.Length > 0)
                .Distinct()
                .ToArray();
            foreach (var name in names)
            {
                if (
                    document.Kind != "venue"
                    || document.Name == name
                    || !_noUnqualifiedReading.Contains((document.VenueId, name))
                )
                {
                    Add(terms, name, new Match(document, name == document.Name));
                }
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
                    if (names.Any(SearchReviewedNicknames.SongLookupNames.Contains))
                    {
                        catalogSongs.Add(
                            new(
                                new(document.ArtistSlug, document.Name, document.Id, false),
                                aliases
                            )
                        );
                    }
                    break;
                case "venue":
                    venuesById[document.VenueId] = document;
                    foreach (
                        var name in names
                            .Where(name => HasDirectPlaceName(document, name))
                            .Append(SearchText.Fold(document.CityLabel))
                            .Where(name =>
                                name.Length > 0
                                && placeAliases?.GetValueOrDefault(name)?.AmbiguousVenues == null
                            )
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
        _reviewedSongs = SearchReviewedNicknames.SongTargets(
            reviewedSetlistSongs ?? FrozenDictionary<(long Id, string ArtistSlug), Guid>.Empty,
            catalogSongs.Concat(
                (indexedNicknameSongs ?? []).Select(song => new SearchReviewedNicknames.CatalogSong(
                    song,
                    []
                ))
            )
        );
        _artistInitialisms = ArtistInitialisms.Build(
            ArtistDocuments.Values,
            _terms
                .Keys.Concat(SearchReviewedNicknames.CompoundSongAliases.Select(rule => rule.Alias))
                .Concat(_reviewedSongs.Select(group => group.Key))
                .Concat(SearchReviewedNicknames.Artists.Select(name => name.Nickname))
                .Concat(
                    songs
                        .Values.Where(song => song.Popularity >= ArtistInitialisms.MinimumSongShows)
                        .Select(song => ArtistInitialisms.TitleInitials(song.Name))
                ),
            reservedInitialismKeys
        );
        _aliasesByVenue = _placeAliases
            .SelectMany(pair => pair.Value.VenueIds.Select(id => (Id: id, Name: pair.Key)))
            .ToLookup(pair => pair.Id, pair => pair.Name, StringComparer.Ordinal);
        _placeArtists = Freeze(
            placeVenues.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.SelectMany(venue => venue.ArtistSlugs).ToHashSet()
            )
        );
        _placeVenues = placeVenues.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray()
        );
        _venuesById = venuesById.ToFrozenDictionary(StringComparer.Ordinal);
        _cityPrefixes = CityPrefixes(venuesById.Values);
        _placePrefixes = PlacePrefixes(placeVenues.Keys);
        _maxPlaceWords = _placeArtists
            .Keys.Concat(_placeAliases.Keys)
            .Concat(_cityPrefixes.Keys)
            .Select(WordCount)
            .DefaultIfEmpty(0)
            .Max();
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
        _curatedSongs = SearchReviewedNicknames
            .CompoundSongAliases.Select(rule =>
                (rule.Alias, Document: FindSong(rule.Artist, rule.Title))
            )
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

    private static FrozenDictionary<string, string[]> PlacePrefixes(IEnumerable<string> names)
    {
        var matches = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var lastWord = name.LastIndexOf(' ');
            if (lastWord < 0)
            {
                continue;
            }

            for (var length = lastWord + 4; length < name.Length; length++)
            {
                Add(matches, name[..length], name);
            }
        }

        // A compound reading verifies a completion against the song or a
        // second place name. Keep multiple spellings of the same city usable.
        return matches.ToFrozenDictionary(match => match.Key, match => match.Value.ToArray());
    }

    public SearchDocument? FindSong(string? artist, string foldedTitle) =>
        artist is null ? null : _songs.GetValueOrDefault((artist, foldedTitle));

    public SearchReviewedNicknames.ArtistNickname? ReadReviewedArtist(string terms, string? scope)
    {
        if (ArtistNames.ContainsKey(terms))
        {
            return null;
        }

        return SearchReviewedNicknames.Artists.FirstOrDefault(name =>
            (scope == null || scope == name.ArtistSlug)
            && ArtistDocuments.TryGetValue(name.ArtistSlug, out var artist)
            // "Widespread Panic 1997" already contains the canonical name.
            // Consuming only "widespread" would leave "panic" as a search term.
            && !(
                SearchInterpretationReader.HasPhrase(artist.Name, name.Nickname)
                && SearchInterpretationReader.HasPhrase(terms, artist.Name)
            )
            && (
                name.LeadingOnly
                    ? terms == name.Nickname
                        || terms.StartsWith(name.Nickname + " ", StringComparison.Ordinal)
                    : SearchInterpretationReader.HasPhrase(terms, name.Nickname)
            )
        );
    }

    public ReviewedSongReading? ReadReviewedSong(
        SearchInput input,
        SearchQuery query,
        IReadOnlySet<string>? selectedVenues = null
    )
    {
        if (
            query.Reading == "literal"
            || input.Scope is not (SearchScope.All or SearchScope.Songs or SearchScope.Shows)
        )
        {
            return null;
        }

        var terms = query.Terms;
        var number = query.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var original = SearchText.Fold(input.Text);
        var replacesYear =
            terms.Length == 0
            && number != null
            && _reviewedSongs.Contains(number)
            && (
                original == number
                || query.Artist != null && original.EndsWith(" " + number, StringComparison.Ordinal)
            );
        if (replacesYear)
        {
            terms = number!;
        }

        var venues = selectedVenues ?? FrozenSet<string>.Empty;
        if (
            !_reviewedSongs.Contains(terms)
            && input.Ignore?.Contains("venue") != true
            && ReadPlaceAlias(terms, query.Artist) is { } place
        )
        {
            terms = place.Remainder;
            venues = place.Place.VenueIds;
        }

        var boosted = (input.BoostArtistIds ?? [])
            .Select(id => ArtistSlugsById.GetValueOrDefault(id))
            .ToHashSet();
        var eligible = _reviewedSongs[terms]
            .Where(song => IncludesReviewedSong(song, query.Artist))
            .Where(song => input.Scope != SearchScope.Songs || song.EntityId != null)
            .Where(song =>
                !song.RequiresArtistContext
                || query.Artist == song.Artist
                || boosted.Contains(song.Artist)
            )
            .ToArray();
        var preferred = eligible.Where(song => boosted.Contains(song.Artist)).ToArray();
        return eligible.Length == 0
            ? null
            : new(
                preferred.Length > 0 ? preferred : eligible,
                venues,
                query.Artist != null || preferred.Length > 0,
                replacesYear
            );
    }

    private bool IncludesReviewedSong(SearchReviewedNicknames.SongTarget song, string? artist) =>
        ArtistDocuments.ContainsKey(song.Artist) && (artist == null || artist == song.Artist);

    public (SearchDocument Artist, string Remainder)? ReadArtistInitialism(
        string terms,
        string? scope
    )
    {
        var separator = terms.IndexOf(' ');
        var token = separator < 0 ? terms : terms[..separator];
        return
            _artistInitialisms.TryGetValue(token, out var artist) && IncludesArtist(artist, scope)
            ? (artist, separator < 0 ? artist.Name : terms[(separator + 1)..])
            : null;
    }

    public string[] ExactSongIds(string title, string? artist) =>
        ExactSongsWithVariants(title)
            .Where(song => IncludesArtist(song, artist))
            .Select(song => song.Id)
            .ToArray();

    private IEnumerable<SearchDocument> ExactSongsWithVariants(string title) =>
        ExactSongs(title).Concat(ExactSongs(AlternateSongTitle(title))).DistinctBy(song => song.Id);

    private IEnumerable<SearchDocument> ExactSongs(string title) =>
        _terms.TryGetValue(title, out var matches)
            ? matches
                .Where(match => match.Canonical && match.Document.Kind == "song")
                .Select(match => match.Document)
            : [];

    // Expand a leading saint abbreviation only when comparing song titles.
    internal static string AlternateSongTitle(string title) =>
        title.StartsWith("st ", StringComparison.Ordinal) ? "saint " + title[3..]
        : title.StartsWith("saint ", StringComparison.Ordinal) ? "st " + title[6..]
        : "";

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

            if (!IncludesArtist(document, artist))
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

        foreach (var song in ExactSongs(AlternateSongTitle(terms)))
        {
            Remember(song, 6, "title_variant");
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

    public string? VerifiedArtistNamePrefix(string terms, string? scope)
    {
        if (
            terms.Length < 4
            || (
                _terms.TryGetValue(terms, out var exact)
                && exact.Any(match => match.Canonical && IncludesArtist(match.Document, scope))
            )
        )
        {
            return null;
        }

        // A whole-word prefix can identify an artist unless the same query
        // already names a catalog entity, such as a song or venue.
        var prefix = terms + " ";
        var matches = ArtistDocuments
            .Values.Where(doc =>
                doc.Popularity > 0
                && IncludesArtist(doc, scope)
                && doc.Name.StartsWith(prefix, StringComparison.Ordinal)
            )
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0].ArtistSlug : null;
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
                var reading in ReadArtistRemainder(left, right, scope, leading: true)
                    .Concat(ReadArtistRemainder(right, left, scope, leading: false))
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
    )> ReadArtistRemainder(string name, string remainder, string? scope, bool leading)
    {
        // Neither a literal alias nor a generated prefix can consume a
        // leading-only nickname at the end of the query.
        if (!leading && SearchReviewedNicknames.LeadingOnlyNames.Contains(name))
        {
            yield break;
        }

        if (!_artistTerms.TryGetValue(name, out var artists))
        {
            if (
                _artistInitialisms.TryGetValue(name, out var initialism)
                && IncludesArtist(initialism, scope)
                && _reviewedSongs[remainder].Any(song => song.Artist == initialism.ArtistSlug)
            )
            {
                yield return (3, initialism.ArtistSlug, remainder, "reviewed song nickname");
                yield break;
            }

            // A fragment from a word in the artist's name needs an exact
            // song to corroborate it. Keep artist-only fragments literal.
            if (
                !name.Contains(' ')
                && (name.Length >= 3 || name.Length == 2 && remainder.Contains(' '))
                && !ArtistInitialisms.IsCommonWord(name)
            )
            {
                var matchingArtists = ExactSongsWithVariants(remainder)
                    .Where(song =>
                        IncludesArtist(song, scope)
                        && ArtistDocuments
                            .GetValueOrDefault(song.ArtistSlug)
                            ?.Name.Split(' ')
                            .Any(word => word.StartsWith(name, StringComparison.Ordinal)) == true
                    )
                    .Select(song => song.ArtistSlug)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                if (matchingArtists is [var artist])
                {
                    yield return (2, artist, remainder, "artist prefix verified by song");
                }
            }
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
                _reviewedSongs[remainder].Any(song => song.Artist == artist)
                || Lookup(remainder, artist, curated: true, limit: 4)
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

        var alternate = AlternateSongTitle(terms);
        if (alternate.Length > 0 && FindSong(artist, alternate) != null)
        {
            return alternate;
        }

        var key = TitleKey(terms);
        return key.Split(' ').Any(word => word.Length >= 4)
            ? _compactTitles.GetValueOrDefault((artist, key))
            : null;
    }

    public bool IsKnownPlace(string place) =>
        _placeAliases.GetValueOrDefault(place)?.VenueIds.Count > 0
        || _cityPrefixes.ContainsKey(place)
        || (_placeArtists.ContainsKey(place) && SearchPlaceAliases.WeakNameReason(place) == null);

    private static FrozenDictionary<string, SearchPlaceAlias> CityPrefixes(
        IEnumerable<SearchDocument> venues
    )
    {
        var prefixes = new List<(string Prefix, string City, SearchDocument Venue)>();
        foreach (var venue in venues)
        {
            var city = SearchText.Fold(venue.CityLabel);
            for (var split = city.IndexOf(' '); split >= 0; split = city.IndexOf(' ', split + 1))
            {
                if (split >= 5)
                {
                    prefixes.Add((city[..split], city, venue));
                }
            }
        }
        return prefixes
            .GroupBy(entry => entry.Prefix, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.City).Distinct().Take(2).Count() == 1)
            .ToFrozenDictionary(
                group => group.Key,
                group => new SearchPlaceAlias(
                    group.First().Venue.CityLabel,
                    group.Select(entry => entry.Venue.VenueId).ToFrozenSet(StringComparer.Ordinal),
                    true
                ),
                StringComparer.Ordinal
            );
    }

    public IEnumerable<string> AliasesAtVenue(string venueId) => _aliasesByVenue[venueId];

    public bool IsVenueAtPlace(string place, string venueId) =>
        venueId.Length > 0
        && (
            VenueAliasFor(place, null)?.Contains(venueId)
            ?? SongPlaces(place).Any(venue => venue.VenueId == venueId)
        );

    public PlaceSongMatch? ExactPlaceSong(string terms, string? artist)
    {
        // Keep the ordinary exact-item ranking when the entire query names a catalog item.
        if (
            _terms.TryGetValue(terms, out var whole)
            && whole.Any(match => match.Canonical && IncludesArtist(match.Document, artist))
        )
        {
            return null;
        }

        for (var split = terms.IndexOf(' '); split >= 0; split = terms.IndexOf(' ', split + 1))
        {
            var left = terms[..split];
            var right = terms[(split + 1)..];
            var match =
                MatchPlaceAndTitle(left, right, artist) ?? MatchPlaceAndTitle(right, left, artist);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IncludesArtist(SearchDocument document, string? artist) =>
        string.IsNullOrEmpty(artist)
        || document.ArtistSlug == artist
        || document.ArtistSlugs.Contains(artist);

    private PlaceSongMatch? MatchPlaceAndTitle(string place, string title, string? artist)
    {
        var songs = ExactSongsWithVariants(title)
            .Where(song => artist == null || song.ArtistSlug == artist)
            .ToArray();
        if (songs.Length == 0)
        {
            return null;
        }

        var artists = songs.Select(song => song.ArtistSlug).ToHashSet(StringComparer.Ordinal);
        var venueIds =
            VenueAliasFor(place, artist)
            ?? SongPlaces(place)
                .Where(venue => venue.ArtistSlugs.Any(artists.Contains))
                .Select(venue => venue.VenueId)
                .ToFrozenSet(StringComparer.Ordinal);
        if (venueIds.Count == 0 && _cityPrefixes.GetValueOrDefault(place) is { } city)
        {
            venueIds = city
                .VenueIds.Where(id =>
                    _venuesById.GetValueOrDefault(id)?.ArtistSlugs.Any(artists.Contains) == true
                )
                .ToFrozenSet(StringComparer.Ordinal);
        }
        return venueIds.Count == 0
            ? null
            : new PlaceSongMatch(title, place, songs.Select(song => song.Id).ToArray(), venueIds);
    }

    private IEnumerable<string> PlaceNames(string place) =>
        (_placePrefixes.GetValueOrDefault(place) ?? []).Prepend(place);

    private IEnumerable<SearchDocument> SongPlaces(string place)
    {
        if (SearchPlaceAliases.WeakNameReason(place) != null)
        {
            return [];
        }

        var named = PlaceNames(place)
            .SelectMany(VenuesFor)
            .Where(venue => HasDirectPlaceName(venue, place))
            .ToArray();
        if (named.Length > 0 || place.Length < 5)
        {
            return named;
        }

        // An exact song can verify a distinctive word or whole-word prefix of
        // the canonical venue name. The prefix need not be a usable alias alone.
        var prefix = place + " ";
        var singleWord = !place.Contains(' ');
        var byName = _venuesById
            .Values.Where(venue =>
                venue.Name.StartsWith(prefix, StringComparison.Ordinal)
                || (
                    singleWord
                    && HasDirectPlaceName(venue, place)
                    && venue.Name.Split(' ').Contains(place, StringComparer.Ordinal)
                )
            )
            .Take(9)
            .ToArray();
        return byName.Length <= 8 ? byName : [];
    }

    private bool HasDirectPlaceName(SearchDocument venue, string name) =>
        venue.Name == name
        || (
            !_noUnqualifiedReading.Contains((venue.VenueId, name))
            && (
                SearchPlaceAliases.WeakNameReason(name) == null
                || _placeAliases.GetValueOrDefault(name)?.VenueIds.Contains(venue.VenueId) == true
            )
        );

    public IReadOnlySet<string> VenuesQualifiedByCity(string terms)
    {
        for (var split = terms.IndexOf(' '); split >= 0; split = terms.IndexOf(' ', split + 1))
        {
            var left = terms[..split];
            var right = terms[(split + 1)..];
            var qualified = AtCity(left, right).Concat(AtCity(right, left)).ToHashSet();
            if (qualified.Count > 0)
            {
                return qualified;
            }
        }

        return FrozenSet<string>.Empty;
    }

    private IEnumerable<string> AtCity(string venueName, string city)
    {
        if (_placeAliases.GetValueOrDefault(venueName)?.AmbiguousVenues is { } targets)
        {
            return QualifyByCity(targets, city)?.VenueIds ?? FrozenSet<string>.Empty;
        }

        if (CityAliasFor(city) is { } alias)
        {
            return VenuesFor(venueName)
                .Select(venue => venue.VenueId)
                .Where(alias.VenueIds.Contains);
        }
        var completed = PlaceNames(city).ToHashSet(StringComparer.Ordinal);
        completed.Remove(venueName);
        return VenuesFor(venueName)
            .Where(venue => completed.Contains(SearchText.Fold(venue.CityLabel)))
            .Select(venue => venue.VenueId);
    }

    public IEnumerable<string> Places(string terms) => PlacePhrases(terms).Where(IsKnownPlace);

    private IEnumerable<string> PlacePhrases(string terms)
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
                yield return string.Join(' ', words, start, count);
            }
        }
    }

    public (string Name, SearchPlaceAlias Place, string Remainder)? ReadPlaceAlias(
        string terms,
        string? artist = null
    )
    {
        (string Name, SearchPlaceAlias Place, string Remainder)? fallback = null;
        foreach (
            var phrase in PlacePhrases(terms)
                .Where(phrase =>
                    _placeAliases.ContainsKey(phrase) || _cityPrefixes.ContainsKey(phrase)
                )
                .OrderByDescending(WordCount)
                .ThenByDescending(value => value.Length)
        )
        {
            var place = _placeAliases.GetValueOrDefault(phrase) ?? _cityPrefixes[phrase];
            var wrapped = $" {terms} ";
            var start = wrapped.IndexOf($" {phrase} ", StringComparison.Ordinal);
            var remainder = (
                wrapped[..start] + " " + wrapped[(start + phrase.Length + 2)..]
            ).Trim();
            if (place.AmbiguousVenues is { } targets)
            {
                if (QualifyByCity(targets, remainder) is { } inCity)
                {
                    return (phrase, new(inCity.Label, inCity.VenueIds, false), "");
                }
                var named = FullArtist(remainder, artist);
                if (
                    QualifyByArtist(
                        targets,
                        named.Kind == "artist_identity" ? named.Artist : artist
                    ) is
                    { } forArtist
                )
                {
                    return (phrase, new(forArtist.Label, forArtist.VenueIds, false), remainder);
                }
                continue;
            }

            if (place.VenueIds.Count > 0)
            {
                var qualified = VenuesFor(remainder)
                    .Select(venue => venue.VenueId)
                    .Where(place.VenueIds.Contains)
                    .ToFrozenSet(StringComparer.Ordinal);
                if (qualified.Count > 0)
                {
                    return (phrase, place with { VenueIds = qualified }, "");
                }
                fallback ??= (phrase, place, remainder);
            }
        }

        return fallback;
    }

    private FrozenSet<string>? VenueAliasFor(string place, string? artist)
    {
        var alias = _placeAliases.GetValueOrDefault(place);
        return alias?.AmbiguousVenues is { } targets
            ? QualifyByArtist(targets, artist)?.VenueIds ?? FrozenSet<string>.Empty
            : alias?.VenueIds;
    }

    private SearchVenueAliasTarget? QualifyByCity(SearchVenueAliasTarget[] targets, string city)
    {
        if (city.Length == 0)
        {
            return null;
        }

        var locality = CityAliasFor(city)?.VenueIds ?? FrozenSet<string>.Empty;
        var matches = targets
            .Where(target => target.City == city || locality.Contains(target.Id))
            .Take(2)
            .ToArray();
        return matches is [var match] ? match : null;
    }

    private SearchPlaceAlias? CityAliasFor(string place) =>
        _placeAliases.GetValueOrDefault(place) is { IsCity: true } alias
            ? alias
            : _cityPrefixes.GetValueOrDefault(place);

    private SearchVenueAliasTarget? QualifyByArtist(
        SearchVenueAliasTarget[] targets,
        string? artist
    )
    {
        if (artist == null)
        {
            return null;
        }

        var matches = targets
            .Where(target =>
                _venuesById.GetValueOrDefault(target.Id)?.ArtistSlugs.Contains(artist) == true
            )
            .Take(2)
            .ToArray();
        return matches is [var match] ? match : null;
    }

    public IEnumerable<SearchDocument> ArtistOptions(string terms, string? scope) =>
        _artistTerms.TryGetValue(terms, out var artists)
            ? artists
                .Where(artist => scope == null || scope == artist)
                .Select(artist => ArtistDocuments[artist])
                .OrderByDescending(artist => artist.Popularity)
            : [];

    public IEnumerable<SearchDocument> VenuesFor(string terms) =>
        _placeVenues.TryGetValue(terms, out var venues) ? venues : [];

    public IEnumerable<SearchDocument> VenueOptions(string terms) =>
        _placeVenues.TryGetValue(terms, out var venues)
            ? venues
                .Where(venue => venue.Name == terms)
                .DistinctBy(venue => venue.VenueId)
                .OrderByDescending(venue => venue.Popularity)
                .ThenBy(venue => venue.VenueId, StringComparer.Ordinal)
            : [];

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
