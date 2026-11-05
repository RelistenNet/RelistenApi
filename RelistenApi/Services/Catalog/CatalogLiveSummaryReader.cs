using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Api.V4;
using Relisten.Data;
using Relisten.Identifiers;
using Relisten.Services.Live;

namespace Relisten.Catalog;

/// <summary>
/// Aggregates public plays and hydrates only the selected recordings in one primary
/// snapshot. Ranking rules are shared with live discovery; legacy DTOs and caches are not.
/// </summary>
public sealed class CatalogLiveSummaryReader(
    DbService db,
    SourceTrackPlaysService plays,
    CatalogShowMetadataReader metadata,
    CatalogTrackNormalizationReader normalization
)
{
    public async Task<LiveSummaryResource> Read(
        LiveSummaryRequest request,
        LiveWindow window,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var activity = await plays.PlaysInLiveWindow(
            window,
            session.Connection,
            session.Transaction,
            publicActivityOnly: true,
            cancellationToken: cancellationToken
        );
        var scored = LiveSummaryRanking.ScoreShows(activity.Shows, request, window);
        var headlines = LiveSummaryRanking.TopShows(scored, request);
        var deepCuts = LiveSummaryRanking.DeepCuts(scored, request);
        var fresh = LiveSummaryRanking.Fresh(scored, request, window);
        var artists = LiveSummaryRanking.TopArtists(activity.Artists, request);
        var topShows = LiveSummaryRanking.TopShowPerArtist(scored);
        var selected = headlines
            .Concat(deepCuts)
            .Concat(fresh)
            .Concat(artists.Select(artist => topShows[artist.Row.artist_id]))
            .DistinctBy(show => show.Row.show_id)
            .ToArray();
        var resources = await ReadShows(session, selected, cancellationToken);
        var totalPlays = activity.AppTypes.Sum(app => app.plays);
        var anniversary = LiveSummaryRanking.OnThisDay(scored, request.MonthDay);
        var anniversaryIds = anniversary.Select(show => show.Row.show_uuid).ToHashSet();

        return new LiveSummaryResource(
            window.Minutes,
            window.Start,
            window.End,
            activity.Shows.Count == 0 ? null : activity.Shows.Max(show => show.last_play_id),
            totalPlays,
            activity.Shows.Sum(show => show.listened_seconds),
            activity
                .AppTypes.Select(app => new LiveAppTypeResource(
                    (PlayAppType)app.app_type,
                    app.plays
                ))
                .ToArray(),
            activity
                .Timeline.Select(bucket => new LiveTimelineResource(
                    bucket.bucket_start,
                    bucket.plays
                ))
                .ToArray(),
            activity.Artists.Count,
            totalPlays - artists.Sum(artist => artist.Row.plays),
            headlines.Select(show => resources[show.Row.show_id]).ToArray(),
            artists
                .Select(artist => new LiveArtistResource(
                    new ArtistId(artist.Row.artist_uuid),
                    artist.Row.plays,
                    artist.Row.listened_seconds,
                    artist.Row.show_count,
                    totalPlays == 0 ? 0 : Math.Round((double)artist.Row.plays / totalPlays, 4),
                    Math.Round(artist.Row.expected_plays, 3),
                    Math.Round(artist.Surprise, 3),
                    resources[topShows[artist.Row.artist_id].Row.show_id]
                ))
                .ToArray(),
            deepCuts.Select(show => resources[show.Row.show_id]).ToArray(),
            fresh.Select(show => resources[show.Row.show_id]).ToArray(),
            new LiveOnThisDayResource(
                request.MonthDay,
                anniversary.Sum(show => show.Row.plays),
                anniversary.Count,
                headlines
                    .Where(show => anniversaryIds.Contains(show.Row.show_uuid))
                    .Select(show => new ShowId(show.Row.show_uuid))
                    .ToArray()
            )
        );
    }

    private async Task<IReadOnlyDictionary<long, LiveShowResource>> ReadShows(
        CatalogReadSession session,
        IReadOnlyList<ScoredShow> selected,
        CancellationToken cancellationToken
    )
    {
        if (selected.Count == 0)
        {
            return new Dictionary<long, LiveShowResource>();
        }

        using var rows = await session.Connection.QueryMultipleAsync(
            session.Command(
                CatalogShowMetadataReader.ShowSelect
                    + " WHERE s.id=ANY(@showIds);"
                    + CatalogRecordingReader.SourceSelect
                    + " WHERE src.id=ANY(@sourceIds);"
                    + CatalogTrackNormalizationReader.TrackSelect
                    + " WHERE t.uuid=ANY(@trackIds) AND NOT t.is_orphaned;",
                new
                {
                    showIds = selected.Select(show => show.Row.show_id).ToArray(),
                    sourceIds = selected.Select(show => show.Row.source_id).Distinct().ToArray(),
                    trackIds = selected.Select(show => show.Row.track_uuid).Distinct().ToArray(),
                },
                cancellationToken
            )
        );
        var shows = (await rows.ReadAsync<CatalogShow>()).ToDictionary(show => show.id);
        var sources = (await rows.ReadAsync<CatalogSource>()).ToDictionary(source => source.id);
        var tracks = (await rows.ReadAsync<CatalogTrack>()).ToDictionary(track => track.uuid);
        foreach (var show in selected)
        {
            shows[show.Row.show_id].Sources = [sources[show.Row.source_id]];
        }
        await metadata.Apply(session, shows.Values.ToArray(), cancellationToken);
        var showsByUuid = shows.Values.ToDictionary(show => show.uuid);
        foreach (
            var batch in tracks.Values.GroupBy(track =>
                showsByUuid[track.show_uuid].normalization_enabled
            )
        )
        {
            await normalization.Apply(session, batch.ToArray(), batch.Key, cancellationToken);
        }

        return selected.ToDictionary(
            show => show.Row.show_id,
            show => new LiveShowResource(
                V4CatalogProjection.ShowSummary(shows[show.Row.show_id]),
                V4CatalogProjection.SourceSummary(sources[show.Row.source_id]),
                new LiveLatestPlayResource(
                    V4CatalogProjection.Track(tracks[show.Row.track_uuid]),
                    show.Row.track_played_at
                ),
                show.Row.plays,
                show.Row.listened_seconds,
                show.Row.plays_30d,
                Math.Round(show.ExpectedPlays, 3),
                Math.Round(show.Surprise, 3),
                show.IsAnniversary,
                show.Row.source_count,
                show.Row.first_played_at,
                show.Row.last_played_at,
                show.Row.timeline
            )
        );
    }
}
