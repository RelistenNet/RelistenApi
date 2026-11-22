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
        var songPlays = await LoadSongPlays(connection, transaction, exportPath, cancellationToken);
        var reviewArtists = await LoadReviewArtists(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CatalogQueryEvidence(collection, vocabulary, passages, songPlays, reviewArtists);
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

    private static async Task<FrozenDictionary<string, long>> LoadSongPlays(
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
            return FrozenDictionary<string, long>.Empty;
        }

        var totals = new Dictionary<string, long>();
        // The catalog matcher returns one edge per canonical song and file,
        // so a reprise within the same file counts its starts once for that song.
        var matches = connection.QueryUnbufferedAsync<SearchProjectionRows.SongMapping>(
            """
            SELECT match.track_uuid AS TrackUuid, match.song_id AS SongId
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
            if (starts.TryGetValue(match.TrackUuid, out var total))
            {
                totals[match.SongId] = totals.GetValueOrDefault(match.SongId) + total.Plays;
            }
        }

        return totals.ToFrozenDictionary();
    }

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
