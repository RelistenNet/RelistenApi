using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Search;

using static SearchText;
using Document = Dictionary<string, object>;

// One projection per build. Read reviews before performances, then read recordings
// and entities after performances have accumulated the source and show play totals.
internal sealed partial class CatalogSearchProjection
{
    private static readonly Dictionary<string, string> ArtistAliases = new()
    {
        ["grateful-dead"] = "gd dead",
        ["billy-strings"] = "billy bmfs",
        ["sci"] = "sci string cheese",
        ["dead-and-co"] = "dead and company dead and co",
        ["jrad"] = "jrad",
    };

    private static readonly Dictionary<string, string> CountryNames = new()
    {
        ["JP"] = "Japan",
        ["US"] = "USA United States",
        ["CA"] = "Canada",
        ["GB"] = "UK United Kingdom England",
        ["FR"] = "France",
        ["DE"] = "Germany",
        ["NL"] = "Netherlands",
        ["AU"] = "Australia",
        ["NZ"] = "New Zealand",
        ["IT"] = "Italy",
        ["ES"] = "Spain",
        ["MX"] = "Mexico",
        ["BR"] = "Brazil",
        ["SE"] = "Sweden",
        ["NO"] = "Norway",
        ["CH"] = "Switzerland",
        ["BE"] = "Belgium",
        ["IE"] = "Ireland",
        ["AR"] = "Argentina",
        ["DK"] = "Denmark",
        ["PT"] = "Portugal",
        ["AT"] = "Austria",
    };

    private static readonly (string Country, string Suffix)[] ImportedCountrySuffixes =
    [
        ("JP", "japan"),
        ("US", "usa"),
        ("CA", "canada"),
        ("GB", "england"),
        ("FR", "france"),
        ("DE", "germany"),
        ("NL", "netherlands"),
        ("AU", "australia"),
    ];

    private readonly SearchProjectionRows _rows;
    private readonly Dictionary<int, ArtistContext> _artists;
    private readonly Dictionary<string, SearchProjectionRows.Venue> _venues;
    private readonly Dictionary<long, SearchProjectionRows.ImportedVenue> _importedVenues;
    private readonly Dictionary<long, SearchProjectionRows.Tour> _tours;
    private readonly Dictionary<string, SearchProjectionRows.Song> _songs;
    private readonly Dictionary<long, ShowContext> _shows;
    private readonly Dictionary<long, RecordingContext> _recordings;
    private readonly Dictionary<Guid, TrackStartTotal> _plays;
    private readonly Dictionary<string, List<string>> _venueArtists;

    public long PlayedFiles { get; private set; }

    private CatalogSearchProjection(
        SearchProjectionRows rows,
        CatalogMetadata metadata,
        Dictionary<Guid, TrackStartTotal> plays
    )
    {
        _rows = rows;
        _plays = plays;
        _artists = metadata.Artists.ToDictionary(
            artist => artist.Id,
            artist => new ArtistContext(
                artist,
                Join(artist.Name, ArtistAliases.GetValueOrDefault(artist.Slug, ""))
            )
        );
        _venues = metadata.Venues.ToDictionary(venue => venue.Id);
        _importedVenues = metadata.ImportedVenues.ToDictionary(venue => venue.Id);
        _tours = metadata.Tours.ToDictionary(tour => tour.Id);
        _songs = metadata.Songs.ToDictionary(song => song.Id);
        _shows = metadata.Shows.ToDictionary(show => show.Id, CreateShowContext);
        _recordings = metadata.Recordings.ToDictionary(
            recording => recording.Id,
            recording => new RecordingContext(recording)
        );
        _venueArtists = _shows
            .Values.GroupBy(show => (string)show.Fields["venue_id"])
            .ToDictionary(
                group => group.Key,
                group => group.Select(show => show.Artist.Row.Slug).ToList()
            );
    }

    public static async Task<CatalogSearchProjection> Open(
        SearchProjectionRows rows,
        Dictionary<Guid, TrackStartTotal> plays,
        CancellationToken cancellationToken
    )
    {
        var metadata = new CatalogMetadata(
            await rows.Artists(cancellationToken),
            await rows.Venues(cancellationToken),
            await rows.ImportedVenues(cancellationToken),
            await rows.Tours(cancellationToken),
            await rows.Songs(cancellationToken),
            await rows.Shows(cancellationToken),
            await rows.Recordings(cancellationToken)
        );
        return new CatalogSearchProjection(rows, metadata, plays);
    }

    public SearchBuildDocuments Documents(CancellationToken cancellationToken) =>
        new(
            Reviews(cancellationToken),
            Performances(cancellationToken),
            Recordings(cancellationToken),
            Entities(cancellationToken)
        );

    public async IAsyncEnumerable<Document> Reviews(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var row in _rows.Reviews().WithCancellation(cancellationToken))
        {
            var show = _shows[row.ShowId];
            var recording = _recordings[row.SourceId];
            var title = Clean(row.Title ?? "");
            var text = string.Join(
                ' ',
                new[] { title, Clean(row.Body ?? "") }.Where(part => part.Length > 0)
            );
            show.Reviews.Add(Fold(text));

            yield return new Document(show.Fields)
            {
                ["id"] = UuidTypeId.Format("review", row.Uuid),
                ["kind"] = "review",
                ["name"] = Fold(title),
                ["label"] = title,
                ["review_text"] = text,
                ["source_id"] = recording.Id,
                ["source_upstream"] = recording.Upstream,
                ["popularity"] = 0,
            };
        }
    }

    public async IAsyncEnumerable<Document> Recordings(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var text in _rows.RecordingTexts().WithCancellation(cancellationToken))
        {
            var recording = _recordings[text.Id];
            var upstream = recording.Upstream;
            var numbers = ArchiveNumber()
                .Matches(upstream)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            var notes = Clean(
                string.Join(
                    ' ',
                    new[] { text.Description, text.TaperNotes, text.Lineage, text.Source }.Where(
                        part => !string.IsNullOrEmpty(part)
                    )
                )
            );

            yield return new Document(_shows[recording.Row.ShowId].Fields)
            {
                ["id"] = recording.Id,
                ["kind"] = "recording",
                ["name"] = Join([upstream, .. numbers]),
                ["label"] = upstream,
                ["aliases"] = Join(numbers),
                ["credits"] = Join(text.Taper ?? "", text.Transferrer ?? ""),
                ["notes"] = notes,
                ["soundboard"] = recording.Row.Soundboard,
                ["matrix"] = Matrix().IsMatch(upstream + " " + notes),
                ["source_upstream"] = upstream,
                ["track_plays"] = recording.Plays,
                ["popularity"] = Math.Round(
                    Math.Log(1d + recording.Plays) + 0.1 * Math.Max(0, recording.Row.Rating),
                    5
                ),
            };
        }
    }

    public async IAsyncEnumerable<Document> Entities(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var catalogEntities = _artists
            .Values.Select(ArtistDocument)
            .Concat(ShowDocuments())
            .Concat(VenueDocuments())
            .Concat(TourDocuments());

        foreach (var document in catalogEntities)
        {
            yield return document;
        }

        await foreach (var song in SongDocuments(cancellationToken))
        {
            yield return song;
        }
    }

    private IEnumerable<Document> ShowDocuments()
    {
        foreach (var show in _shows.Values)
        {
            var date = show.Row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            yield return new Document(show.Fields)
            {
                ["id"] = show.Fields["show_id"],
                ["kind"] = "show",
                ["label"] = $"{show.Artist.Row.Name} · {date} · {show.Fields["venue_label"]}",
                ["name"] = Fold((string)show.Fields["venue_label"]),
                ["aliases"] = Fold(date),
                ["popularity"] = Math.Round(Math.Log(1d + show.Plays), 5),
            };
        }
    }

    private IEnumerable<Document> VenueDocuments() =>
        _venues
            .Values.Where(venue => string.IsNullOrEmpty(venue.MergedInto))
            .Select(VenueDocument)
            .Concat(
                _importedVenues
                    .Values.Where(venue => !string.IsNullOrEmpty(venue.Name))
                    .Select(ImportedVenueDocument)
            );

    private IEnumerable<Document> TourDocuments()
    {
        foreach (var tour in _tours.Values)
        {
            var artist = _artists[tour.ArtistId];
            yield return new Document
            {
                ["id"] = UuidTypeId.Format("tour", tour.Uuid),
                ["kind"] = "tour",
                ["label"] = tour.Name,
                ["name"] = Fold(tour.Name),
                ["artist_slug"] = artist.Row.Slug,
                ["artist"] = artist.SearchName,
                ["popularity"] = 0,
            };
        }
    }

    private async IAsyncEnumerable<Document> SongDocuments(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var approvedArtists = _artists
            .Values.Where(artist => artist.Row.Approved)
            .ToDictionary(artist => artist.Row.Uuid);
        var canonicalNames = new HashSet<(int Artist, string Name)>();
        foreach (var song in _songs.Values)
        {
            if (
                !string.IsNullOrEmpty(song.MergedInto)
                || !approvedArtists.TryGetValue(song.ArtistUuid, out var artist)
            )
            {
                continue;
            }

            foreach (var name in song.Aliases.Prepend(song.Name))
            {
                canonicalNames.Add((artist.Row.Id, Fold(name)));
            }

            var document = SongDocument(song.Id, song.Name, song.Shows, artist);
            document["aliases"] = Join(song.Aliases);
            yield return document;
        }

        await foreach (var song in _rows.ImportedSongs().WithCancellation(cancellationToken))
        {
            if (!canonicalNames.Contains((song.ArtistId, Fold(song.Name))))
            {
                yield return SongDocument(
                    UuidTypeId.Format("imported_song", song.Uuid),
                    song.Name,
                    song.Shows,
                    _artists[song.ArtistId]
                );
            }
        }
    }

    private ShowContext CreateShowContext(SearchProjectionRows.Show show)
    {
        var artist = _artists[show.ArtistId];
        var venue = ResolveShowVenue(show);
        var tour = show.TourId is long tourId ? _tours.GetValueOrDefault(tourId)?.Name ?? "" : "";
        var run = SearchableRun(show, artist.Row.Slug, venue.Label, tour);

        return new ShowContext(
            show,
            artist,
            new Document
            {
                ["show_id"] = UuidTypeId.Format("show", show.Uuid),
                ["artist_slug"] = artist.Row.Slug,
                ["artist"] = artist.SearchName,
                ["artist_label"] = artist.Row.Name,
                ["date"] = show.Date.Year * 10_000 + show.Date.Month * 100 + show.Date.Day,
                ["day_key"] = show.Date.Month * 100 + show.Date.Day,
                ["run"] = run,
                ["tour_label"] = tour,
                ["place"] = venue.Place,
                ["country"] = venue.Country,
                ["venue_label"] = venue.Label,
                ["city_label"] = venue.City,
                ["venue_id"] = venue.Id,
            }
        );
    }

    private ShowVenue ResolveShowVenue(SearchProjectionRows.Show show)
    {
        var imported = show.VenueId is long id ? _importedVenues.GetValueOrDefault(id) : null;
        var canonical = CurrentVenue(show.CanonicalVenueId);
        var location = imported?.Location ?? "";
        var label = canonical?.Name ?? imported?.Name ?? "";
        var venueId =
            canonical?.Id
            ?? (imported == null ? "" : UuidTypeId.Format("imported_venue", imported.Uuid));
        var city = string.IsNullOrEmpty(canonical?.City)
            ? (location.Contains(',') ? FirstPlacePart(location) : "")
            : canonical.City;
        var country = VenueCountry(canonical?.Country, location);
        var aliases = canonical?.Aliases.Take(8).ToArray() ?? [imported?.PastNames ?? ""];
        var place = Join([
            label,
            imported?.Name ?? "",
            city,
            canonical?.Region ?? "",
            location,
            CountryNames.GetValueOrDefault(country, ""),
            .. aliases,
        ]);

        return new ShowVenue(venueId, label, city, country, place);
    }

    private static string VenueCountry(string? canonicalCountry, string importedLocation)
    {
        if (!string.IsNullOrEmpty(canonicalCountry))
        {
            return canonicalCountry.ToUpperInvariant();
        }

        var location = Fold(importedLocation);
        return ImportedCountrySuffixes
                .FirstOrDefault(pair => location.EndsWith(pair.Suffix, StringComparison.Ordinal))
                .Country
            ?? "";
    }

    private static string SearchableRun(
        SearchProjectionRows.Show show,
        string artistSlug,
        string venue,
        string tour
    )
    {
        // Listeners know the Garden run as Baker's Dozen. The imported tour
        // label does not reliably contain that name for each date in the run.
        if (
            artistSlug == "phish"
            && show.Date >= new DateOnly(2017, 7, 21)
            && show.Date <= new DateOnly(2017, 8, 6)
            && Fold(venue).Contains("madison square garden", StringComparison.Ordinal)
        )
        {
            return Join(tour, "Baker’s Dozen");
        }

        return Fold(tour);
    }

    private sealed record ShowVenue(
        string Id,
        string Label,
        string City,
        string Country,
        string Place
    );

    private SearchProjectionRows.Venue? CurrentVenue(string? id)
    {
        // Bound traversal if malformed venue merges form a cycle.
        for (var depth = 0; depth < 32 && !string.IsNullOrEmpty(id); depth++)
        {
            var venue = _venues.GetValueOrDefault(id);
            if (string.IsNullOrEmpty(venue?.MergedInto))
            {
                return venue;
            }

            id = venue.MergedInto;
        }

        return null;
    }

    private static string FirstPlacePart(string location)
    {
        var comma = location.IndexOf(',');
        return (comma < 0 ? location : location[..comma]).Trim();
    }

    private static Document ArtistDocument(ArtistContext artist) =>
        new()
        {
            ["id"] = UuidTypeId.Format("artist", artist.Row.Uuid),
            ["kind"] = "artist",
            ["label"] = artist.Row.Name,
            ["name"] = Fold(artist.Row.Name),
            ["artist_slug"] = artist.Row.Slug,
            ["aliases"] = Join(
                ArtistAliases.GetValueOrDefault(artist.Row.Slug, ""),
                artist.Row.Slug
            ),
            ["popularity"] = 0,
        };

    private Document VenueDocument(SearchProjectionRows.Venue venue)
    {
        var artists = _venueArtists.GetValueOrDefault(venue.Id) ?? [];
        var country = venue.Country ?? "";
        return new Document
        {
            ["id"] = venue.Id,
            ["kind"] = "venue",
            ["label"] = venue.Name,
            ["name"] = Fold(venue.Name),
            ["aliases"] = Join(venue.Aliases),
            ["place"] = Join(
                venue.City ?? "",
                venue.Region ?? "",
                CountryNames.GetValueOrDefault(country, "")
            ),
            ["city_label"] = venue.City ?? "",
            ["venue_label"] = venue.Name,
            ["venue_id"] = venue.Id,
            ["country"] = country,
            ["artist_slugs"] = artists
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ["popularity"] = Math.Log(1d + artists.Count),
        };
    }

    private Document ImportedVenueDocument(SearchProjectionRows.ImportedVenue venue)
    {
        var id = UuidTypeId.Format("imported_venue", venue.Uuid);
        var artist = _artists.GetValueOrDefault(venue.ArtistId);
        return new Document
        {
            ["id"] = id,
            ["kind"] = "venue",
            ["label"] = venue.Name!,
            ["name"] = Fold(venue.Name!),
            ["aliases"] = Fold(venue.PastNames ?? ""),
            ["artist_slug"] = artist?.Row.Slug ?? "",
            ["artist"] = artist?.SearchName ?? "",
            ["place"] = Fold(venue.Location ?? ""),
            ["venue_label"] = venue.Name!,
            ["venue_id"] = id,
            ["city_label"] = FirstPlacePart(venue.Location ?? ""),
            ["popularity"] = Math.Log(1d + (_venueArtists.GetValueOrDefault(id)?.Count ?? 0)),
        };
    }

    private static Document SongDocument(string id, string name, int shows, ArtistContext artist) =>
        new()
        {
            ["id"] = id,
            ["kind"] = "song",
            ["label"] = name,
            ["name"] = Fold(name),
            ["artist_slug"] = artist.Row.Slug,
            ["artist"] = artist.SearchName,
            ["artist_label"] = artist.Row.Name,
            ["popularity"] = Math.Log(1d + shows),
            ["song_id"] = id,
        };

    private sealed record CatalogMetadata(
        IEnumerable<SearchProjectionRows.Artist> Artists,
        IEnumerable<SearchProjectionRows.Venue> Venues,
        IEnumerable<SearchProjectionRows.ImportedVenue> ImportedVenues,
        IEnumerable<SearchProjectionRows.Tour> Tours,
        IEnumerable<SearchProjectionRows.Song> Songs,
        IEnumerable<SearchProjectionRows.Show> Shows,
        IEnumerable<SearchProjectionRows.Recording> Recordings
    );

    private sealed record ArtistContext(SearchProjectionRows.Artist Row, string SearchName);

    private sealed class ShowContext(
        SearchProjectionRows.Show row,
        ArtistContext artist,
        Document fields
    )
    {
        public SearchProjectionRows.Show Row { get; } = row;
        public ArtistContext Artist { get; } = artist;
        public Document Fields { get; } = fields;
        public List<string> Reviews { get; } = [];
        public long Plays { get; set; }
    }

    private sealed class RecordingContext(SearchProjectionRows.Recording row)
    {
        public SearchProjectionRows.Recording Row { get; } = row;
        public string Id { get; } = UuidTypeId.Format("source", row.Uuid);
        public string Upstream { get; } = row.Upstream ?? "";
        public long Plays { get; set; }
    }
}
