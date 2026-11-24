using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Controllers.V4;
using Relisten.Services.Popularity;

namespace Relisten.Search;

using Document = Dictionary<string, object>;

/// <summary>Build-time v4 metadata reads within the search projection's catalog snapshot.</summary>
internal sealed class CatalogSearchResourceProjection(
    CatalogReadSession session,
    ILoggerFactory loggerFactory
)
{
    private readonly CatalogPopularityMetadataReader _popularity = new();
    private readonly CatalogVenueMetadataReader _venues = new(
        loggerFactory.CreateLogger<CatalogVenueMetadataReader>()
    );
    private readonly CatalogTourMetadataReader _tours = new();
    private readonly CatalogTrackNormalizationReader _trackNormalization = new(
        new CatalogSongMetadataReader(loggerFactory.CreateLogger<CatalogSongMetadataReader>()),
        loggerFactory.CreateLogger<CatalogTrackNormalizationReader>()
    );

    public Task<IReadOnlyList<CatalogArtist>> Artists(Guid[] artistIds, CancellationToken token) =>
        CatalogBrowseReader.ReadArtists(session, artistIds, _popularity, token);

    public async Task<Dictionary<Guid, CatalogShow>> Shows(Guid[] showIds, CancellationToken token)
    {
        var reader = new CatalogShowMetadataReader(
            new CatalogImportedVenueMetadataReader(),
            _venues,
            new CatalogArtistVenueMetadataReader(),
            _tours,
            _popularity
        );
        var shows = await CatalogEntityReader.ReadShows(session, showIds, token);
        await reader.Apply(session, shows, token);
        return shows.ToDictionary(show => show.uuid);
    }

    public Task<IReadOnlyDictionary<Guid, CatalogTour>> Tours(
        Guid[] tourIds,
        CancellationToken token
    ) => _tours.Read(session, tourIds, token);

    public Task<IReadOnlyDictionary<string, CatalogVenue>> Venues(
        string[] venueIds,
        CancellationToken token
    ) => _venues.Read(session, venueIds, token);

    public async Task<Dictionary<long, CatalogImportedVenue>> ImportedVenues(
        long[] venueIds,
        CancellationToken token
    ) =>
        (await CatalogEntityReader.ReadImportedVenues(session, [], venueIds, token)).ToDictionary(
            venue => venue.id
        );

    public async Task<Dictionary<string, CatalogSongDetail>> Songs(
        string[] songIds,
        CancellationToken token
    ) =>
        (
            await CatalogSongReader.ReadSongDetails(
                session,
                null,
                songIds,
                new CatalogSongMatchReader(),
                token
            )
        ).ToDictionary(song => song.id, StringComparer.Ordinal);

    public Task ApplyImportedSongMappings(
        IReadOnlyList<CatalogImportedSong> songs,
        CancellationToken token
    ) => new CatalogImportedSongMappingReader().Apply(session, songs, token);

    public async Task<Dictionary<Guid, CatalogTrack>> Tracks(
        IReadOnlyList<Guid> trackIds,
        IReadOnlySet<Guid> approvedArtists,
        CancellationToken token
    )
    {
        var tracks = await CatalogEntityReader.ReadTracks(session, trackIds.ToArray(), token);
        foreach (var group in tracks.GroupBy(track => approvedArtists.Contains(track.artist_uuid)))
        {
            await _trackNormalization.Apply(session, group.ToArray(), group.Key, token);
        }

        return tracks.ToDictionary(track => track.uuid);
    }

    public Task<Dictionary<(Guid Show, string Song), PopularityMetrics>> PerformancePopularity(
        Guid[] shows,
        CancellationToken cancellationToken
    ) =>
        PopularityStore.ReadPerformanceMetrics(
            session.Connection,
            shows,
            session.Transaction,
            cancellationToken
        );

    public Task<Dictionary<Guid, PopularityMetrics>> RecordingPopularity(
        Guid[] sources,
        CancellationToken cancellationToken
    ) =>
        PopularityStore.ReadRecordingMetrics(
            session.Connection,
            sources,
            session.Transaction,
            cancellationToken
        );

    public static JsonElement Serialize(object resource)
    {
        using var document = JsonDocument.Parse(CatalogJsonResult.Serialize(resource));
        return document.RootElement.Clone();
    }

    public static void AttachPopularity(Document document, PopularityMetrics? metrics) =>
        AttachPopularity(
            document,
            metrics is null ? null : V4CatalogProjection.ProjectPopularity(metrics)
        );

    public static void AttachPopularity(Document document, PopularityResource? popularity)
    {
        if (popularity is not null)
        {
            document["display_popularity"] = Serialize(popularity);
        }
    }

    public Task<Dictionary<string, PopularityMetrics>> SongPopularity(
        string[] ids,
        CancellationToken token
    ) => PopularityStore.ReadSongMetrics(session.Connection, ids, session.Transaction, token);
}
