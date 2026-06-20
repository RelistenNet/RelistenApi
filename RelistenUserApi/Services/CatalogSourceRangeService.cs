using Dapper;

namespace Relisten.UserApi.Services;

public sealed class CatalogSourceRangeService
{
    private readonly UserApiDbService _db;

    public CatalogSourceRangeService(UserApiDbService db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Guid>> ResolveSourceTrackUuids(
        Guid sourceUuid,
        int startTrackPosition,
        int endTrackPosition)
    {
        if (startTrackPosition < 1 || endTrackPosition < startTrackPosition)
        {
            throw new PlaylistOperationException("invalid_source_range");
        }

        await using var connection = _db.CreateConnection();
        var tracks = (await connection.QueryAsync<SourceRangeTrackRow>(
            """
            SELECT
                t.uuid AS "SourceTrackUuid",
                t.track_position AS "TrackPosition"
            FROM public.sources s
            JOIN public.source_tracks t ON t.source_id = s.id
            WHERE s.uuid = @SourceUuid
              AND t.track_position BETWEEN @StartTrackPosition AND @EndTrackPosition
              AND t.is_orphaned = FALSE
            ORDER BY t.track_position
            """,
            new
            {
                SourceUuid = sourceUuid,
                StartTrackPosition = startTrackPosition,
                EndTrackPosition = endTrackPosition
            })).ToList();

        var expectedCount = endTrackPosition - startTrackPosition + 1;
        if (tracks.Count != expectedCount ||
            !tracks.Select(track => track.TrackPosition)
                .SequenceEqual(Enumerable.Range(startTrackPosition, expectedCount)))
        {
            throw new PlaylistOperationException("invalid_source_range");
        }

        return tracks.Select(track => track.SourceTrackUuid).ToList();
    }

    private sealed class SourceRangeTrackRow
    {
        public required Guid SourceTrackUuid { get; init; }
        public required int TrackPosition { get; init; }
    }
}
