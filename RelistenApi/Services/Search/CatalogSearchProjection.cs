using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Api.Models;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Search;

using static SearchText;
using Document = Dictionary<string, object>;

// One projection per build. Read reviews before performances, then read recordings
// and entities after performances have accumulated the source and show play totals.
internal sealed partial class CatalogSearchProjection
{
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
    private readonly CatalogSearchResourceProjection _resources;
    private readonly HashSet<Guid> _approvedArtists;
    private Dictionary<long, CatalogImportedVenue>? _importedVenueResources;
    private readonly Dictionary<int, ArtistContext> _artists;
    private readonly Dictionary<string, SearchProjectionRows.Venue> _venues;
    private readonly Dictionary<long, SearchProjectionRows.ImportedVenue> _importedVenues;
    private readonly Dictionary<long, SearchProjectionRows.Tour> _tours;
    private readonly Dictionary<string, SearchProjectionRows.Song> _songs;
    private readonly Dictionary<long, ShowContext> _shows;
    private readonly Dictionary<long, RecordingContext> _recordings;
    private readonly Dictionary<Guid, TrackStartTotal> _plays;
    private readonly Dictionary<string, List<string>> _venueArtists;
    private readonly Dictionary<int, int> _artistShowCounts;

    public long PlayedFiles { get; private set; }

    private CatalogSearchProjection(
        SearchProjectionRows rows,
        CatalogMetadata metadata,
        Dictionary<Guid, TrackStartTotal> plays,
        ILoggerFactory loggerFactory
    )
    {
        _rows = rows;
        _resources = new(rows.DisplaySession, loggerFactory);
        _plays = plays;
        _approvedArtists = metadata
            .Artists.Where(artist => artist.Approved)
            .Select(artist => artist.Uuid)
            .ToHashSet();
        _artists = metadata.Artists.ToDictionary(
            artist => artist.Id,
            artist => new ArtistContext(
                artist,
                Join(artist.Name, SearchReviewedNicknames.IndexedArtistText(artist.Slug))
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
        _artistShowCounts = _shows
            .Values.GroupBy(show => show.Artist.Row.Id)
            .ToDictionary(group => group.Key, group => group.Count());
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
        CancellationToken cancellationToken,
        ILoggerFactory? loggerFactory = null
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
        return new CatalogSearchProjection(
            rows,
            metadata,
            plays,
            loggerFactory ?? NullLoggerFactory.Instance
        );
    }

    // Full builds calibrate each kind against its entire population before
    // streaming display documents. Artist reindexes keep the published scale.
    public SearchBuildDocuments Documents(
        CancellationToken cancellationToken,
        bool calibrate = true
    ) =>
        new(
            Reviews(cancellationToken),
            Performances(cancellationToken, calibrate),
            Recordings(cancellationToken, calibrate),
            Entities(cancellationToken, calibrate)
        );

    private static IReadOnlyDictionary<double, double> Percentiles(
        string kind,
        IEnumerable<double> signals
    )
    {
        var histogram = new SearchPercentiles();
        foreach (var signal in signals)
        {
            histogram.Observe(kind, SignalBucket(signal));
        }
        return histogram.Calculate().GetValueOrDefault(kind) ?? [];
    }

    private static Document WithPercentile(
        Document document,
        IReadOnlyDictionary<double, double>? ranks
    )
    {
        var rank = ranks?.GetValueOrDefault(
            SignalBucket(Convert.ToDouble(document["ranking_signal"]))
        );
        if (rank is > 0)
        {
            document["popularity_percentile"] = rank.Value;
        }
        return document;
    }

    // Preserve the single-precision buckets used by the published calibration.
    private static double SignalBucket(double signal) => (float)signal;

    public async IAsyncEnumerable<Document> Reviews(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var row in _rows.Reviews().WithCancellation(cancellationToken))
        {
            var title = Clean(row.Title ?? "");
            var text = string.Join(
                ' ',
                new[] { title, Clean(row.Body ?? "") }.Where(part => part.Length > 0)
            );
            if (text.Length == 0)
            {
                continue;
            }

            var show = _shows[row.ShowId];
            var recording = _recordings[row.SourceId];
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
                ["ranking_signal"] = 0,
                ["display"] = CatalogSearchResourceProjection.Serialize(
                    V4CatalogProjection.SourceReview(
                        new CatalogSourceReview
                        {
                            uuid = row.Uuid,
                            source_uuid = recording.Row.Uuid,
                            title = row.Title,
                            review = row.Body!,
                            author = row.Author,
                            rating = row.Rating,
                            created_at = row.CreatedAt,
                            updated_at = row.UpdatedAt,
                        }
                    )
                ),
            };
        }
    }

    public async IAsyncEnumerable<Document> Recordings(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool calibrate = false
    )
    {
        var ranks = calibrate
            ? Percentiles("recording", _recordings.Values.Select(row => (double)row.Plays))
            : null;
        var venues = await ImportedVenueResources(cancellationToken);
        foreach (var ids in _recordings.Keys.Order().Chunk(ShowBatchSize))
        {
            var sources = await _rows.RecordingTexts(ids, cancellationToken);
            var popularity = await _resources.RecordingPopularity(
                sources.Select(source => source.uuid).ToArray(),
                cancellationToken
            );
            foreach (var source in sources)
            {
                yield return WithPercentile(
                    RecordingDocument(source, venues, popularity.GetValueOrDefault(source.uuid)),
                    ranks
                );
            }
        }
    }

    private Document RecordingDocument(
        CatalogSource source,
        IReadOnlyDictionary<long, CatalogImportedVenue> venues,
        PopularityMetrics? popularity
    )
    {
        var recording = _recordings[source.id];
        var upstream = recording.Upstream;
        var numbers = ArchiveNumber()
            .Matches(upstream)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var notes = Clean(
            string.Join(
                ' ',
                new[]
                {
                    source.description,
                    source.taper_notes,
                    source.lineage,
                    source.source,
                }.Where(part => !string.IsNullOrEmpty(part))
            )
        );

        source.ImportedVenue = source.venue_id is long venueId
            ? venues.GetValueOrDefault(venueId)
            : null;
        var document = new Document(_shows[recording.Row.ShowId].Fields)
        {
            ["id"] = recording.Id,
            ["kind"] = "recording",
            ["name"] = Join([upstream, .. numbers]),
            ["label"] = upstream,
            ["aliases"] = Join(numbers),
            ["credits"] = Join(source.taper ?? "", source.transferrer ?? ""),
            // Who made the recording tells it apart from the show's others.
            ["taper_label"] = source.taper ?? "",
            ["transferrer_label"] = source.transferrer ?? "",
            ["notes"] = notes,
            ["soundboard"] = recording.Row.Soundboard,
            ["matrix"] = Matrix().IsMatch(upstream + " " + notes),
            ["source_upstream"] = upstream,
            ["track_plays"] = recording.Plays,
            ["ranking_signal"] = recording.Plays,
            // Shown on the result as the recording page shows them; ranking uses the weighted rating.
            ["avg_rating"] = recording.Row.AvgRating,
            ["num_reviews"] = recording.Row.NumReviews,
            ["display"] = CatalogSearchResourceProjection.Serialize(
                V4CatalogProjection.SourceSummary(source)
            ),
            ["popularity"] = Math.Round(
                Math.Log(1d + recording.Plays) + 0.1 * Math.Max(0, recording.Row.Rating),
                5
            ),
        };
        CatalogSearchResourceProjection.AttachPopularity(document, popularity);
        return document;
    }

    public async IAsyncEnumerable<Document> Entities(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool calibrate = false
    )
    {
        var entities = ArtistDocuments(calibrate, cancellationToken)
            .Concat(ShowDocuments(calibrate, cancellationToken))
            .Concat(VenueDocuments(calibrate, cancellationToken))
            .Concat(TourDocuments(cancellationToken))
            .Concat(SongDocuments(calibrate, cancellationToken))
            .Concat(PassageTrackDocuments(cancellationToken));

        await foreach (var document in entities.WithCancellation(cancellationToken))
        {
            yield return document;
        }
    }

    private async IAsyncEnumerable<Document> ArtistDocuments(
        bool calibrate,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var ranks = calibrate
            ? Percentiles("artist", _artists.Keys.Select(id => (double)ArtistShowCount(id)))
            : null;
        var artists = await _resources.Artists(
            _artists.Values.Select(artist => artist.Row.Uuid).ToArray(),
            cancellationToken
        );
        var byUuid = artists.ToDictionary(artist => artist.uuid);
        foreach (var artist in _artists.Values)
        {
            var document = ArtistDocument(artist);
            var resource = CatalogBrowseProjection.Artist(byUuid[artist.Row.Uuid]);
            document["display"] = CatalogSearchResourceProjection.Serialize(resource);
            CatalogSearchResourceProjection.AttachPopularity(document, resource.Popularity);
            yield return WithPercentile(document, ranks);
        }
    }

    private async IAsyncEnumerable<Document> ShowDocuments(
        bool calibrate,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var ranks = calibrate
            ? Percentiles("show", _shows.Values.Select(show => (double)show.Plays))
            : null;
        foreach (var batch in _shows.Values.Chunk(ShowBatchSize))
        {
            var showResources = await _resources.Shows(
                batch.Select(show => show.Row.Uuid).ToArray(),
                cancellationToken
            );
            foreach (var show in batch)
            {
                var resource = V4CatalogProjection.ShowSummary(showResources[show.Row.Uuid]);
                var document = ShowDocument(show);
                document["display"] = CatalogSearchResourceProjection.Serialize(resource);
                CatalogSearchResourceProjection.AttachPopularity(document, resource.Popularity);
                yield return WithPercentile(document, ranks);
            }
        }
    }

    private static Document ShowDocument(ShowContext show)
    {
        var date = show.Row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new Document(show.Fields)
        {
            ["id"] = show.Fields["show_id"],
            ["kind"] = "show",
            ["label"] = $"{show.Artist.Row.Name} · {date} · {show.Fields["venue_label"]}",
            ["name"] = Fold((string)show.Fields["venue_label"]),
            ["aliases"] = Fold(date),
            ["track_plays"] = show.Plays,
            ["ranking_signal"] = show.Plays,
            ["avg_rating"] = show.Row.AvgRating,
            ["popularity"] = Math.Round(Math.Log(1d + show.Plays), 5),
        };
    }

    private async IAsyncEnumerable<Document> VenueDocuments(
        bool calibrate,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var live = _rows.ArtistId is null
            ? _venues.Values.Where(venue => string.IsNullOrEmpty(venue.MergedInto)).ToArray()
            : [];
        var importedResources = await ImportedVenueResources(cancellationToken);
        var imported = _importedVenues
            .Values.Where(venue =>
                !string.IsNullOrEmpty(venue.Name)
                && (_rows.ArtistId is null || venue.ArtistId == _rows.ArtistId)
                && importedResources.ContainsKey(venue.Id)
            )
            .ToArray();
        var ranks = calibrate
            ? Percentiles(
                "venue",
                live.Select(venue => VenueSignal(VenueShowCount(venue.Id)))
                    .Concat(
                        imported.Select(venue =>
                            VenueSignal(
                                VenueShowCount(UuidTypeId.Format("imported_venue", venue.Uuid))
                            )
                        )
                    )
            )
            : null;
        if (live.Length > 0)
        {
            var venueResources = await _resources.Venues(
                live.Select(row => row.Id).ToArray(),
                cancellationToken
            );
            foreach (var venue in live)
            {
                var artists = _venueArtists.GetValueOrDefault(venue.Id) ?? [];
                yield return WithPercentile(
                    VenueDocument(venue, artists, artists.Count, venueResources[venue.Id]),
                    ranks
                );
            }
        }

        foreach (var venue in imported)
        {
            yield return WithPercentile(
                ImportedVenueDocument(venue, importedResources[venue.Id]),
                ranks
            );
        }
    }

    public IEnumerable<string> SharedVenues => _venueArtists.Keys.Where(_venues.ContainsKey);

    public async Task<IReadOnlyList<Document>> SharedVenueDocuments(
        IReadOnlySet<string> affectedIds,
        CancellationToken cancellationToken
    )
    {
        if (affectedIds.Count == 0)
        {
            return [];
        }

        // A show can still point at a retired venue ID. Include its membership
        // under the same surviving venue as the show projection.
        var affectedSurvivors = affectedIds
            .Select(id => SearchProjectionRows.CurrentVenue(_venues, id)?.Id)
            .OfType<string>()
            .ToHashSet();
        var survivors = _venues
            .Keys.Select(id =>
                (Id: id, Current: SearchProjectionRows.CurrentVenue(_venues, id)?.Id)
            )
            .Where(venue => venue.Current != null && affectedSurvivors.Contains(venue.Current))
            .ToDictionary(venue => venue.Id, venue => venue.Current!);
        var venueResources = await _resources.Venues(
            affectedSurvivors.ToArray(),
            cancellationToken
        );
        var memberships = await _rows.VenueMemberships(survivors.Keys.ToArray(), cancellationToken);
        var bySurvivor = memberships.ToLookup(row => survivors[row.VenueId]);
        return survivors
            .Values.Distinct()
            .Select(id =>
                VenueDocument(
                    _venues[id],
                    bySurvivor[id].Select(row => row.ArtistSlug),
                    bySurvivor[id].Sum(row => row.Shows),
                    venueResources[id]
                )
            )
            .ToArray();
    }

    private async IAsyncEnumerable<Document> TourDocuments(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var resources = await _resources.Tours(
            _tours.Values.Select(tour => tour.Uuid).ToArray(),
            cancellationToken
        );
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
                ["ranking_signal"] = 0,
                ["display"] = CatalogSearchResourceProjection.Serialize(
                    V4CatalogProjection.Tour(resources[tour.Uuid])
                ),
            };
        }
    }

    private async IAsyncEnumerable<Document> SongDocuments(
        bool calibrate,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var approvedArtists = _artists
            .Values.Where(artist => artist.Row.Approved)
            .ToDictionary(artist => artist.Row.Uuid);
        var songs = _songs
            .Values.Where(song =>
                string.IsNullOrEmpty(song.MergedInto)
                && approvedArtists.ContainsKey(song.ArtistUuid)
            )
            .ToArray();
        var canonicalNames = songs
            .SelectMany(song =>
                song.Aliases.Prepend(song.Name)
                    .Select(name => (approvedArtists[song.ArtistUuid].Row.Id, Fold(name)))
            )
            .ToHashSet();
        var imported = (await _rows.ImportedSongs(cancellationToken))
            .Where(song => !canonicalNames.Contains((song.ArtistId, Fold(song.Name))))
            .Select(song => (Song: song, Id: UuidTypeId.Format("imported_song", song.Uuid)))
            .ToArray();
        var resources = await _resources.Songs(
            songs.Select(song => song.Id).ToArray(),
            cancellationToken
        );
        var ids = songs.Select(song => song.Id).Concat(imported.Select(row => row.Id)).ToArray();
        var popularity = await _resources.SongPopularity(ids, cancellationToken);
        var ranks = calibrate
            ? Percentiles(
                "song",
                ids.Select(id => (double)SongSignal(popularity.GetValueOrDefault(id)))
            )
            : null;
        foreach (var song in songs)
        {
            var artist = approvedArtists[song.ArtistUuid];
            var metrics = popularity.GetValueOrDefault(song.Id);
            var document = SongDocument(song.Id, song.Name, song.Shows, artist, metrics);
            document["aliases"] = Join(song.Aliases);
            document["display"] = CatalogSearchResourceProjection.Serialize(
                V4CatalogProjection.SongDetail(resources[song.Id])
            );
            CatalogSearchResourceProjection.AttachPopularity(document, metrics);
            yield return WithPercentile(document, ranks);
        }

        foreach (var batch in imported.Chunk(ShowBatchSize))
        {
            var display = batch.Select(row => ImportedSongFacts(row.Song)).ToArray();
            await _resources.ApplyImportedSongMappings(display, cancellationToken);
            for (var index = 0; index < batch.Length; index++)
            {
                var (song, id) = batch[index];
                var metrics = popularity.GetValueOrDefault(id);
                var document = SongDocument(
                    id,
                    song.Name,
                    song.Shows,
                    _artists[song.ArtistId],
                    metrics
                );
                document["display"] = CatalogSearchResourceProjection.Serialize(
                    V4CatalogProjection.ImportedSong(display[index])
                );
                CatalogSearchResourceProjection.AttachPopularity(document, metrics);
                yield return WithPercentile(document, ranks);
            }
        }
    }

    private static CatalogImportedSong ImportedSongFacts(SearchProjectionRows.ImportedSong song) =>
        new()
        {
            id = song.Id,
            uuid = song.Uuid,
            artist_uuid = song.ArtistUuid,
            name = song.Name,
            slug = song.Slug,
            upstream_identifier = song.UpstreamIdentifier,
            setlist_show_count = song.Shows,
            created_at = song.CreatedAt,
            updated_at = song.UpdatedAt,
        };

    private async Task<Dictionary<long, CatalogImportedVenue>> ImportedVenueResources(
        CancellationToken cancellationToken
    )
    {
        if (_importedVenueResources is not null)
        {
            return _importedVenueResources;
        }

        var sourceVenues = await _rows.SourceVenueIds(cancellationToken);
        _importedVenueResources = await _resources.ImportedVenues(
            _importedVenues.Keys.Union(sourceVenues).ToArray(),
            cancellationToken
        );
        return _importedVenueResources;
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
                ["location_label"] = venue.LocationLabel,
                ["venue_id"] = venue.Id,
            }
        );
    }

    private ShowVenue ResolveShowVenue(SearchProjectionRows.Show show)
    {
        var imported = show.VenueId is long id ? _importedVenues.GetValueOrDefault(id) : null;
        var canonical = SearchProjectionRows.CurrentVenue(_venues, show.CanonicalVenueId);
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

        // Artist-specific browse shows the imported location when present.
        var locationLabel = string.IsNullOrEmpty(location)
            ? canonical?.LocationLabel ?? ""
            : location;
        return new ShowVenue(venueId, label, city, country, place, locationLabel);
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
        string Place,
        string LocationLabel
    );

    private static string FirstPlacePart(string location)
    {
        var comma = location.IndexOf(',');
        return (comma < 0 ? location : location[..comma]).Trim();
    }

    private int ArtistShowCount(int artistId) => _artistShowCounts.GetValueOrDefault(artistId);

    private int VenueShowCount(string venueId) =>
        _venueArtists.GetValueOrDefault(venueId)?.Count ?? 0;

    private static double VenueSignal(int showCount) => Math.Log(1d + showCount);

    private Document ArtistDocument(ArtistContext artist) =>
        new()
        {
            ["id"] = UuidTypeId.Format("artist", artist.Row.Uuid),
            ["kind"] = "artist",
            ["label"] = artist.Row.Name,
            ["name"] = Fold(artist.Row.Name),
            ["artist_slug"] = artist.Row.Slug,
            ["ranking_signal"] = ArtistShowCount(artist.Row.Id),
            ["aliases"] = Join(
                SearchReviewedNicknames.IndexedArtistText(artist.Row.Slug),
                artist.Row.Slug
            ),
            ["popularity"] = 0,
        };

    private static Document VenueDocument(
        SearchProjectionRows.Venue venue,
        IEnumerable<string> artists,
        int showCount,
        CatalogVenue resource
    )
    {
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
            ["location_label"] = venue.LocationLabel,
            ["venue_label"] = venue.Name,
            ["venue_id"] = venue.Id,
            ["country"] = country,
            ["artist_slugs"] = artists
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ["popularity"] = VenueSignal(showCount),
            ["ranking_signal"] = VenueSignal(showCount),
            ["display"] = CatalogSearchResourceProjection.Serialize(
                V4CatalogProjection.Venue(resource)
            ),
        };
    }

    private Document ImportedVenueDocument(
        SearchProjectionRows.ImportedVenue venue,
        CatalogImportedVenue resource
    )
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
            ["location_label"] = venue.Location ?? "",
            ["popularity"] = VenueSignal(VenueShowCount(id)),
            ["ranking_signal"] = VenueSignal(VenueShowCount(id)),
            ["display"] = CatalogSearchResourceProjection.Serialize(
                V4CatalogProjection.ImportedVenue(resource)
            ),
        };
    }

    private static Document SongDocument(
        string id,
        string name,
        int shows,
        ArtistContext artist,
        Relisten.Api.Models.PopularityMetrics? metrics
    ) =>
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
            ["ranking_signal"] = SongSignal(metrics),
            ["song_id"] = id,
        };

    private static long SongSignal(PopularityMetrics? metrics) => metrics?.windows.Year?.plays ?? 0;

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
