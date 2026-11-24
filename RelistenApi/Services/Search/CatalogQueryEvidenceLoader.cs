using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Search;

using static SearchText;

internal static class CatalogQueryEvidenceLoader
{
    public static async Task<CatalogQueryEvidence> Load(
        DbService db,
        TypesenseClient typesense,
        string collection,
        string performances,
        string? exportPath,
        CancellationToken cancellationToken
    )
    {
        // Catalog metadata, marked paths and play totals must agree within one
        // snapshot. The primary lets the refresh finish even during replica replay.
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );

        var rows = new SearchProjectionRows(connection, transaction);
        var shortNames = (await rows.ShortVenueNames(cancellationToken)).ToArray();
        var aliasKeys = shortNames
            .SelectMany(row => SearchPlaceAliases.Keys(row.Name, row.Normalized))
            .ToHashSet(StringComparer.Ordinal);
        // Use the index's popularity scale and check short venue names against
        // indexed artists and songs, including imported songs absent from norm.
        var (percentiles, indexedSongPlays, reservedInitialismKeys, placeCollisions) =
            await LoadIndexedVocabulary(typesense, collection, aliasKeys, cancellationToken);
        var venues = (await rows.Venues(cancellationToken)).ToDictionary(venue => venue.Id);
        var placeAliases = SearchPlaceAliases.Build(
            venues,
            shortNames,
            SearchReviewedPlaceNames.CityNames,
            await rows.ImportedPlacesForAliases(cancellationToken),
            placeCollisions
        );
        var vocabulary = await LoadVocabulary(
            rows,
            venues,
            percentiles,
            reservedInitialismKeys,
            placeAliases,
            await LoadNicknameSongs(typesense, collection, performances, cancellationToken),
            cancellationToken
        );
        var trackStarts = await SearchPlayTotals.Load(
            connection,
            transaction,
            DateOnly.FromDateTime(DateTime.UtcNow),
            exportPath,
            cancellationToken
        );
        var passages = await SearchPassages.Load(
            connection,
            transaction,
            vocabulary,
            venues,
            trackStarts,
            cancellationToken
        );
        await RemoveMappedTracks(connection, transaction, trackStarts, cancellationToken);
        var titlePlays = new Dictionary<(string Artist, string Title), long>();
        await AddUnmappedTitleStarts(
            connection,
            transaction,
            trackStarts,
            titlePlays,
            cancellationToken
        );
        var reviewArtists = await LoadReviewArtists(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CatalogQueryEvidence(
            collection,
            vocabulary,
            passages,
            indexedSongPlays.ToFrozenDictionary(),
            reviewArtists,
            titlePlays.ToFrozenDictionary()
        );
    }

    internal static async Task<(
        Dictionary<string, double> Percentiles,
        Dictionary<string, long> SongPlays,
        HashSet<string> ReservedInitialismKeys,
        HashSet<string> PlaceCollisions
    )> LoadIndexedVocabulary(
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

        return (percentiles, songPlays, reservedInitialismKeys, placeCollisions);
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

            slugs.Add(artists[show.ArtistId].Slug);
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

    private static async Task RemoveMappedTracks(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Dictionary<Guid, TrackStartTotal> starts,
        CancellationToken cancellationToken
    )
    {
        if (starts.Count == 0)
        {
            return;
        }

        // A matched file is represented by its published song totals. Keep
        // only unmatched title evidence for artists without a song identity.
        var matches = connection.QueryUnbufferedAsync<Guid>(
            """
            SELECT DISTINCT match.track_uuid
            FROM artists artist JOIN features feature ON feature.artist_id = artist.id AND feature.norm_catalogue
            CROSS JOIN LATERAL norm.song_recording_matches(artist.uuid, NULL::text[]) match
            """,
            transaction: transaction,
            commandTimeout: 600
        );
        await foreach (var track in matches.WithCancellation(cancellationToken))
        {
            starts.Remove(track);
        }
    }

    private static async Task AddUnmappedTitleStarts(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyDictionary<Guid, TrackStartTotal> starts,
        Dictionary<(string Artist, string Title), long> titles,
        CancellationToken cancellationToken
    )
    {
        // Imported song documents have no canonical song ID. Use the same
        // folded title as the playable raw-track projection for their totals.
        var tracks = connection.QueryUnbufferedAsync<RawTitleTrack>(
            """
            SELECT track.uuid AS TrackUuid, artist.slug AS Artist, track.title AS Title
            FROM source_tracks track
            JOIN artists artist ON artist.id = track.artist_id
            JOIN sources source ON source.id = track.source_id AND source.artist_id = artist.id
            JOIN shows show ON show.id = source.show_id AND show.artist_id = artist.id
            JOIN source_sets set ON set.id = track.source_set_id AND set.source_id = source.id
            WHERE track.uuid = ANY(@TrackIds)
              AND NOT track.is_orphaned AND nullif(track.title, '') IS NOT NULL
              AND (nullif(track.mp3_url, '') IS NOT NULL OR nullif(track.flac_url, '') IS NOT NULL)
            """,
            new { TrackIds = starts.Keys.ToArray() },
            transaction: transaction,
            commandTimeout: 600
        );
        await foreach (var track in tracks.WithCancellation(cancellationToken))
        {
            var key = (track.Artist, Fold(RawTitle(track.Title)));
            titles[key] = titles.GetValueOrDefault(key) + starts[track.TrackUuid].Plays;
        }
    }

    private sealed record RawTitleTrack(Guid TrackUuid, string Artist, string Title);

    private static async Task<FrozenSet<string>> LoadReviewArtists(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken
    ) =>
        (
            await connection.QueryAsync<string>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT artist.slug
                    FROM source_reviews review JOIN sources source ON source.id = review.source_id
                    JOIN shows show ON show.id = source.show_id AND show.artist_id = source.artist_id
                    JOIN artists artist ON artist.id = show.artist_id
                    """,
                    transaction: transaction,
                    commandTimeout: 120,
                    cancellationToken: cancellationToken
                )
            )
        ).ToFrozenSet();
}
