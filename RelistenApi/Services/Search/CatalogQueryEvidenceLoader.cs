using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Identifiers;

namespace Relisten.Search;

using static SearchText;

internal static class CatalogQueryEvidenceLoader
{
    public static async Task<CatalogQueryEvidence> Load(
        DbService db,
        TypesenseClient typesense,
        string collection,
        string performances,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var timer = Stopwatch.StartNew();
        await using var connection = db.CreateReplicaConnection(
            SearchEvidenceRead.CommandTimeoutSeconds
        );
        await connection.OpenAsync(cancellationToken);

        var rows = new SearchProjectionRows(connection, transaction: null);
        var shortNames = (await rows.ShortVenueNames(cancellationToken)).ToArray();
        var aliasKeys = shortNames
            .SelectMany(row => SearchPlaceAliases.Keys(row.Name, row.Normalized))
            .ToHashSet(StringComparer.Ordinal);
        var setupMs = timer.ElapsedMilliseconds;
        timer.Restart();
        // Use the index's popularity scale and check short venue names against
        // indexed artists and songs, including imported songs absent from norm.
        var indexed = await LoadIndexedVocabulary(
            typesense,
            collection,
            aliasKeys,
            cancellationToken
        );
        var nicknameSongs = await LoadNicknameSongs(
            typesense,
            collection,
            performances,
            cancellationToken
        );
        var exportsMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var venues = (await rows.Venues(cancellationToken)).ToDictionary(venue => venue.Id);
        var placeAliases = SearchPlaceAliases.Build(
            venues,
            shortNames,
            SearchReviewedPlaceNames.CityNames,
            await rows.ImportedPlacesForAliases(cancellationToken),
            indexed.PlaceCollisions
        );
        var vocabulary = await LoadVocabulary(
            rows,
            venues,
            indexed.Percentiles,
            indexed.ReservedInitialismKeys,
            placeAliases,
            nicknameSongs,
            cancellationToken
        );
        var reviewArtists = await rows.ReviewArtists(cancellationToken);
        var metadataMs = setupMs + timer.ElapsedMilliseconds;
        timer.Restart();
        var passages = await SearchPassages.Load(
            connection,
            transaction: null,
            vocabulary,
            venues,
            cancellationToken
        );
        logger.LogInformation(
            "Read query evidence for {Collection}: Typesense exports {ExportMs} ms, catalog lookups {MetadataMs} ms, passages and popularity {PassageMs} ms",
            collection,
            exportsMs,
            metadataMs,
            timer.ElapsedMilliseconds
        );
        return new CatalogQueryEvidence(
            collection,
            vocabulary,
            passages,
            indexed.SongPlays.ToFrozenDictionary(),
            reviewArtists.ToFrozenSet(),
            indexed.ImportedTitlePlays.ToFrozenDictionary()
        );
    }

    private static async Task<IndexedVocabulary> LoadIndexedVocabulary(
        TypesenseClient typesense,
        string collection,
        IReadOnlySet<string> placeNames,
        CancellationToken cancellationToken
    )
    {
        // The HTTP client timeout ends at the headers for this streaming export.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var percentiles = new Dictionary<string, double>(StringComparer.Ordinal);
        var songPlays = new Dictionary<string, long>(StringComparer.Ordinal);
        var importedTitlePlays = new Dictionary<(string Artist, string Title), long>();
        var reservedInitialismKeys = new HashSet<string>(StringComparer.Ordinal);
        var placeCollisions = new HashSet<string>(StringComparer.Ordinal);
        var establishedSongPopularity = Math.Log(1d + ArtistInitialisms.MinimumSongShows);
        await foreach (
            var row in typesense.ExportDocuments(
                collection,
                "kind:=[artist,song,venue]",
                deadline.Token,
                includeVocabularyFields: true
            )
        )
        {
            if (row.PopularityPercentile > 0)
            {
                percentiles.Add(row.Id, row.PopularityPercentile);
            }

            if (
                row.Kind == "song"
                && row.DisplayPopularity?.Windows?.GetValueOrDefault("1y") is { Plays: > 0 } year
            )
            {
                songPlays.Add(row.Id, year.Plays);
                if (
                    row.Id.StartsWith("imported_song_", StringComparison.Ordinal)
                    && row.ArtistSlug is { } artist
                    && row.Name is { } title
                )
                {
                    var key = (artist, title);
                    importedTitlePlays[key] =
                        importedTitlePlays.GetValueOrDefault(key) + year.Plays;
                }
            }
            if (row.Name is { } name)
            {
                ArtistInitialisms.ReserveName(reservedInitialismKeys, name);
                if (row.Kind == "song" && row.Popularity >= establishedSongPopularity)
                {
                    // Derived song initials reserve a token. Search uses only
                    // the song aliases that are actually in the vocabulary.
                    ArtistInitialisms.ReserveName(
                        reservedInitialismKeys,
                        ArtistInitialisms.TitleInitials(name)
                    );
                }
                if (row.Kind is ("artist" or "song") && placeNames.Contains(name))
                {
                    placeCollisions.Add(name);
                }
            }
        }

        return new(
            percentiles,
            songPlays,
            importedTitlePlays,
            reservedInitialismKeys,
            placeCollisions
        );
    }

    private static async Task<SearchReviewedNicknames.SongTarget[]> LoadNicknameSongs(
        TypesenseClient typesense,
        string entities,
        string performances,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var targets = new HashSet<SearchReviewedNicknames.SongTarget>();
        var songNames = SearchRequests.Names(SearchReviewedNicknames.SongLookupNames);
        await foreach (
            var song in typesense.ExportSongNames(
                entities,
                $"kind:=song && {songNames}",
                deadline.Token
            )
        )
        {
            if (song.Id.StartsWith("imported_song_", StringComparison.Ordinal))
            {
                targets.Add(new(song.ArtistSlug, song.Name, song.Id, MatchTrackTitle: true));
            }
        }

        // Some artists only have performance titles. Read the reviewed titles
        // in bulk and collapse them to one name per artist.
        var trackNames = SearchRequests.Names(SearchReviewedNicknames.SongTitles);
        await foreach (
            var track in typesense.ExportSongNames(performances, trackNames, deadline.Token)
        )
        {
            if (string.IsNullOrEmpty(track.SongId))
            {
                targets.Add(new(track.ArtistSlug, track.Name, null, MatchTrackTitle: true));
            }
        }
        return targets.ToArray();
    }

    private static async Task<SearchVocabulary> LoadVocabulary(
        SearchProjectionRows rows,
        IReadOnlyDictionary<string, SearchProjectionRows.Venue> venues,
        IReadOnlyDictionary<string, double> percentiles,
        HashSet<string> reservedInitialismKeys,
        SearchPlaceAliases.Readings placeAliases,
        SearchReviewedNicknames.SongTarget[] indexedNicknameSongs,
        CancellationToken cancellationToken
    )
    {
        var artists = (await rows.Artists(cancellationToken)).ToDictionary(artist => artist.Id);
        var artistsByUuid = artists.Values.ToDictionary(artist => artist.Uuid);
        var shows = await rows.Shows(cancellationToken);
        var songs = await rows.Songs(cancellationToken);
        var tours = await rows.Tours(cancellationToken);

        var artistShows = new Dictionary<int, int>();
        var venueShows = new Dictionary<string, int>();
        var venueArtists = new Dictionary<string, HashSet<string>>();
        foreach (var show in shows)
        {
            // A newly replayed artist can appear in shows after the artist read.
            if (!artists.TryGetValue(show.ArtistId, out var artist))
            {
                continue;
            }

            artistShows[show.ArtistId] = artistShows.GetValueOrDefault(show.ArtistId) + 1;
            if (SearchProjectionRows.CurrentVenue(venues, show.CanonicalVenueId) is not { } venue)
            {
                continue;
            }

            var venueId = venue.Id;
            venueShows[venueId] = venueShows.GetValueOrDefault(venueId) + 1;
            if (!venueArtists.TryGetValue(venueId, out var slugs))
            {
                slugs = [];
                venueArtists.Add(venueId, slugs);
            }

            slugs.Add(artist.Slug);
        }

        var entries = artists
            .Values.Select(artist => ArtistEntry(artist, artistShows.GetValueOrDefault(artist.Id)))
            .Concat(
                songs
                    .Where(song =>
                        string.IsNullOrEmpty(song.MergedInto)
                        && artistsByUuid.GetValueOrDefault(song.ArtistUuid)?.Approved == true
                    )
                    .Select(song => SongEntry(song, artistsByUuid[song.ArtistUuid]))
            )
            .Concat(
                venues
                    .Values.Where(venue =>
                        string.IsNullOrEmpty(venue.MergedInto) && venueShows.ContainsKey(venue.Id)
                    )
                    .Select(venue =>
                        VenueEntry(venue, venueShows[venue.Id], venueArtists[venue.Id])
                    )
            )
            .Select(entry =>
                (
                    entry.Document with
                    {
                        PopularityPercentile = percentiles.GetValueOrDefault(entry.Document.Id),
                    },
                    entry.Aliases
                )
            );
        var tourDocuments = tours
            .Where(tour => artists.ContainsKey(tour.ArtistId))
            .Select(tour => new SearchDocument
            {
                Id = UuidTypeId.Format("tour", tour.Uuid),
                Kind = "tour",
                Label = tour.Name,
                Name = Fold(tour.Name),
                ArtistSlug = artists[tour.ArtistId].Slug,
            });
        var setlistSongs = await rows.ReviewedSetlistSongs(cancellationToken);
        return new SearchVocabulary(
            entries,
            tourDocuments,
            reservedInitialismKeys,
            placeAliases.Aliases,
            placeAliases.NoUnqualifiedReading,
            setlistSongs.ToDictionary(song => (song.Id, song.ArtistSlug), song => song.Uuid),
            indexedNicknameSongs
        );
    }

    private static (SearchDocument Document, string[] Aliases) ArtistEntry(
        SearchProjectionRows.Artist artist,
        int shows
    )
    {
        var aliases = SearchReviewedNicknames.ArtistNameAliases(artist.Slug).ToArray();
        return (
            new SearchDocument
            {
                Id = UuidTypeId.Format("artist", artist.Uuid),
                Kind = "artist",
                Label = artist.Name,
                Name = Fold(artist.Name),
                ArtistSlug = artist.Slug,
                Popularity = shows,
                Aliases = string.Join(' ', aliases),
            },
            aliases
        );
    }

    private static (SearchDocument Document, string[] Aliases) SongEntry(
        SearchProjectionRows.Song song,
        SearchProjectionRows.Artist artist
    )
    {
        var aliases = song.Aliases.Select(Fold).ToArray();
        return (
            new SearchDocument
            {
                Id = song.Id,
                Kind = "song",
                SongId = song.Id,
                Label = song.Name,
                Name = Fold(song.Name),
                ArtistSlug = artist.Slug,
                ArtistLabel = artist.Name,
                Popularity = song.Shows,
                Aliases = string.Join(' ', aliases),
            },
            aliases
        );
    }

    private static (SearchDocument Document, string[] Aliases) VenueEntry(
        SearchProjectionRows.Venue venue,
        int shows,
        HashSet<string> artists
    )
    {
        var aliases = venue.Aliases.Select(Fold).ToArray();
        return (
            new SearchDocument
            {
                Id = venue.Id,
                Kind = "venue",
                VenueId = venue.Id,
                Label = venue.Name,
                Name = Fold(venue.Name),
                VenueLabel = venue.Name,
                CityLabel = venue.City ?? "",
                LocationLabel = venue.LocationLabel,
                ArtistSlugs = artists.Order(StringComparer.Ordinal).ToImmutableArray(),
                Popularity = shows,
                Aliases = string.Join(' ', aliases),
            },
            aliases
        );
    }

    private sealed record IndexedVocabulary(
        Dictionary<string, double> Percentiles,
        Dictionary<string, long> SongPlays,
        Dictionary<(string Artist, string Title), long> ImportedTitlePlays,
        HashSet<string> ReservedInitialismKeys,
        HashSet<string> PlaceCollisions
    );
}
