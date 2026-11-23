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
        string collection,
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

        var vocabulary = await LoadVocabulary(
            new SearchProjectionRows(connection, transaction),
            cancellationToken
        );
        var passages = await SearchPassages.Load(
            connection,
            transaction,
            vocabulary,
            cancellationToken
        );
        var (songPlays, titlePlays, soundcheckOnlyPlays) = await LoadPlayback(
            connection,
            transaction,
            exportPath,
            cancellationToken
        );
        var reviewArtists = await LoadReviewArtists(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CatalogQueryEvidence(
            collection,
            vocabulary,
            passages,
            songPlays,
            reviewArtists,
            titlePlays,
            soundcheckOnlyPlays
        );
    }

    private static async Task<SearchVocabulary> LoadVocabulary(
        SearchProjectionRows rows,
        CancellationToken cancellationToken
    )
    {
        var artists = (await rows.Artists(cancellationToken)).ToDictionary(artist => artist.Id);
        var artistsByUuid = artists.Values.ToDictionary(artist => artist.Uuid);
        var shows = await rows.Shows(cancellationToken);
        var venues = await rows.Venues(cancellationToken);
        var songs = await rows.Songs(cancellationToken);
        var tours = await rows.Tours(cancellationToken);

        var artistShows = new Dictionary<int, int>();
        var venueShows = new Dictionary<string, int>();
        var venueArtists = new Dictionary<string, HashSet<string>>();
        foreach (var show in shows)
        {
            artistShows[show.ArtistId] = artistShows.GetValueOrDefault(show.ArtistId) + 1;
            if (show.CanonicalVenueId is not { Length: > 0 } venueId)
            {
                continue;
            }

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
                    .Where(venue =>
                        string.IsNullOrEmpty(venue.MergedInto) && venueShows.ContainsKey(venue.Id)
                    )
                    .Select(venue =>
                        VenueEntry(venue, venueShows[venue.Id], venueArtists[venue.Id])
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
        return new SearchVocabulary(entries, tourDocuments);
    }

    private static (SearchDocument Document, string[] Aliases) ArtistEntry(
        SearchProjectionRows.Artist artist,
        int shows
    )
    {
        var aliases = SearchVocabularyRules
            .ArtistAliases.Where(rule => rule.Value == artist.Slug)
            .Select(rule => rule.Key)
            .ToArray();
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
                ArtistSlugs = artists.Order(StringComparer.Ordinal).ToImmutableArray(),
                Popularity = shows,
                Aliases = string.Join(' ', aliases),
            },
            aliases
        );
    }

    private static async Task<(
        FrozenDictionary<string, long> Songs,
        FrozenDictionary<(string Artist, string Title), long> Titles,
        FrozenDictionary<string, long> Soundchecks
    )> LoadPlayback(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string? exportPath,
        CancellationToken cancellationToken
    )
    {
        var through = DateOnly.FromDateTime(DateTime.UtcNow);
        var starts = await SearchPlayTotals.Load(
            connection,
            transaction,
            through,
            exportPath,
            cancellationToken
        );
        if (starts.Count == 0)
        {
            return (
                FrozenDictionary<string, long>.Empty,
                FrozenDictionary<(string, string), long>.Empty,
                FrozenDictionary<string, long>.Empty
            );
        }

        var titles = new Dictionary<(string Artist, string Title), long>();
        var (songs, soundchecks) = await ReadMappedSongStarts(
            connection,
            transaction,
            starts,
            cancellationToken
        );
        await AddUnmappedTitleStarts(connection, transaction, starts, titles, cancellationToken);
        return (
            songs.ToFrozenDictionary(),
            titles.ToFrozenDictionary(),
            soundchecks.ToFrozenDictionary()
        );
    }

    private static async Task<(
        Dictionary<string, long> Songs,
        Dictionary<string, long> Soundchecks
    )> ReadMappedSongStarts(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Dictionary<Guid, TrackStartTotal> starts,
        CancellationToken cancellationToken
    )
    {
        var songs = new Dictionary<string, long>();
        var mapped = new HashSet<Guid>();
        var concertPerformances = new HashSet<(string Song, Guid Show)>();
        var soundchecks = new Dictionary<(string Song, Guid Show), long>();
        // The catalog matcher returns one edge per canonical song and file,
        // so a reprise within the same file counts its starts once for that song.
        var matches = connection.QueryUnbufferedAsync<MappedSongTrack>(
            """
            SELECT match.track_uuid AS TrackUuid, match.song_id AS SongId,
                   match.show_uuid AS ShowUuid, track.title AS Title
            FROM artists artist JOIN features feature ON feature.artist_id = artist.id AND feature.norm_catalogue
            CROSS JOIN LATERAL norm.song_recording_matches(artist.uuid, NULL::text[]) match
            JOIN source_tracks track ON track.uuid = match.track_uuid
            WHERE nullif(track.mp3_url, '') IS NOT NULL OR nullif(track.flac_url, '') IS NOT NULL
            """,
            transaction: transaction,
            commandTimeout: 600
        );
        await foreach (var match in matches.WithCancellation(cancellationToken))
        {
            var performance = (match.SongId, match.ShowUuid);
            var soundcheck = Soundcheck().IsMatch(match.Title ?? "");
            if (!soundcheck)
            {
                concertPerformances.Add(performance);
            }

            if (!starts.TryGetValue(match.TrackUuid, out var total) || total.Plays <= 0)
            {
                continue;
            }

            songs[match.SongId] = songs.GetValueOrDefault(match.SongId) + total.Plays;
            mapped.Add(match.TrackUuid);
            if (soundcheck)
            {
                soundchecks[performance] = soundchecks.GetValueOrDefault(performance) + total.Plays;
            }
        }

        // Mapped files have already contributed to their canonical title.
        foreach (var id in mapped)
        {
            starts.Remove(id);
        }

        // A performance is marked soundcheck only when every file is a soundcheck.
        // The venue rule keeps its existing all-files song totals.
        var soundcheckOnlyPlays = soundchecks
            .Where(pair => !concertPerformances.Contains(pair.Key))
            .GroupBy(pair => pair.Key.Song)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));
        return (songs, soundcheckOnlyPlays);
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
            if (Soundcheck().IsMatch(track.Title))
            {
                continue;
            }

            var key = (track.Artist, Fold(RawTitle(track.Title)));
            titles[key] = titles.GetValueOrDefault(key) + starts[track.TrackUuid].Plays;
        }
    }

    private sealed record RawTitleTrack(Guid TrackUuid, string Artist, string Title);

    private sealed record MappedSongTrack(
        Guid TrackUuid,
        string SongId,
        Guid ShowUuid,
        string? Title
    );

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
