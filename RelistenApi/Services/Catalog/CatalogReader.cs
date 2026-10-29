using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Relisten.Catalog;

/// <summary>
/// Reads recording facts and optional approved interpretations in one primary
/// snapshot. It never invokes ingestion, normalization jobs or legacy services.
/// </summary>
public sealed class CatalogReader(DbService db, ILogger<CatalogReader> logger)
{
    /// <summary>Returns a complete recording hierarchy, or null when the show is unknown.</summary>
    public async Task<CatalogShow?> ReadShow(
        Guid uuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var show = await session.Connection.QuerySingleOrDefaultAsync<CatalogShow>(
            session.Command(
                """
                SELECT s.id, s.uuid, s.venue_id, s.date::timestamp AS date,
                       s.avg_rating, s.avg_duration, s.display_date, s.created_at, s.updated_at,
                       a.uuid artist_uuid, y.uuid year_uuid, t.uuid tour_uuid,
                       COALESCE(f.norm_catalogue, false) normalization_enabled,
                       COALESCE(cnt.source_count, 0) source_count,
                       COALESCE(cnt.has_soundboard_source, false) has_soundboard_source,
                       COALESCE(cnt.has_flac, false) has_streamable_flac_source,
                       COALESCE(cnt.max_updated_at, s.updated_at) most_recent_source_updated_at
                FROM shows s JOIN artists a ON a.id=s.artist_id
                LEFT JOIN features f ON f.artist_id=a.id
                LEFT JOIN years y ON y.id=s.year_id LEFT JOIN tours t ON t.id=s.tour_id
                LEFT JOIN show_source_information cnt ON cnt.show_id=s.id
                WHERE s.uuid=@uuid
                """,
                new { uuid },
                cancellationToken
            )
        );
        if (show is null)
        {
            return null;
        }

        await ReadRecordings(session, show, cancellationToken);
        await ReadShowMetadata(session, show, cancellationToken);
        await ApplyNormalization(
            session,
            show.Sources.SelectMany(x => x.Sets).SelectMany(x => x.Tracks).ToArray(),
            show.normalization_enabled,
            cancellationToken
        );
        return show;
    }

    /// <summary>Returns one non-orphaned playable file with exact recording parents.</summary>
    public async Task<CatalogTrack?> ReadTrack(
        Guid uuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var track = await session.Connection.QuerySingleOrDefaultAsync<CatalogTrack>(
            session.Command(
                TrackSelect + " WHERE t.uuid=@uuid AND NOT t.is_orphaned",
                new { uuid },
                cancellationToken
            )
        );
        if (track is null)
        {
            return null;
        }

        var approved = await session.Connection.QuerySingleAsync<bool>(
            session.Command(
                "SELECT COALESCE(f.norm_catalogue,false) FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artist_uuid",
                new { track.artist_uuid },
                cancellationToken
            )
        );
        await ApplyNormalization(session, [track], approved, cancellationToken);
        return track;
    }

    private static async Task ReadRecordings(
        CatalogReadSession session,
        CatalogShow show,
        CancellationToken cancellationToken
    )
    {
        var sources = (
            await session.Connection.QueryAsync<CatalogSource>(
                session.Command(
                    """
                    SELECT src.*, a.uuid artist_uuid, sh.uuid show_uuid,
                           COALESCE(rc.source_review_count,0) review_count
                    FROM sources src JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                    LEFT JOIN source_review_counts rc ON rc.source_id=src.id
                    WHERE sh.uuid=@uuid ORDER BY src.avg_rating_weighted DESC, src.uuid
                    """,
                    new { show.uuid },
                    cancellationToken
                )
            )
        ).ToList();
        var sets = (
            await session.Connection.QueryAsync<CatalogSet>(
                session.Command(
                    """
                    SELECT sets.*, src.uuid source_uuid, a.uuid artist_uuid
                    FROM source_sets sets JOIN sources src ON src.id=sets.source_id JOIN artists a ON a.id=src.artist_id
                    WHERE src.show_id=@id ORDER BY sets.index, sets.uuid
                    """,
                    new { show.id },
                    cancellationToken
                )
            )
        ).ToArray();
        var tracks = (
            await session.Connection.QueryAsync<CatalogTrack>(
                session.Command(
                    TrackSelect
                        + " WHERE sh.uuid=@uuid AND NOT t.is_orphaned ORDER BY t.track_position,t.uuid",
                    new { show.uuid },
                    cancellationToken
                )
            )
        ).ToArray();
        var links = (
            await session.Connection.QueryAsync<CatalogLink>(
                session.Command(
                    """
                    SELECT l.*, src.uuid source_uuid, up.name provider FROM links l JOIN sources src ON src.id=l.source_id
                    JOIN upstream_sources up ON up.id=l.upstream_source_id
                    WHERE src.show_id=@id ORDER BY l.id
                    """,
                    new { show.id },
                    cancellationToken
                )
            )
        ).ToArray();
        var tracksBySet = tracks.ToLookup(x => x.source_set_id);
        var setsBySource = sets.ToLookup(x => x.source_id);
        var linksBySource = links.ToLookup(x => x.source_id);
        foreach (var set in sets)
        {
            set.Tracks = tracksBySet[set.id].ToList();
        }
        foreach (var source in sources)
        {
            source.Sets = setsBySource[source.id].ToList();
            source.Links = linksBySource[source.id].ToList();
        }
        show.Sources = sources;
    }

    private async Task ReadShowMetadata(
        CatalogReadSession session,
        CatalogShow show,
        CancellationToken cancellationToken
    )
    {
        var venues = (
            await session.Connection.QueryAsync<CatalogImportedVenue>(
                session.Command(
                    """
                    SELECT v.*, a.uuid artist_uuid FROM venues v JOIN artists a ON a.id=v.artist_id
                    WHERE v.id=@venue_id OR v.id IN (SELECT venue_id FROM sources WHERE show_id=@id)
                    """,
                    new { show.venue_id, show.id },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.id);
        show.ImportedVenue = show.venue_id is { } venueId
            ? venues.GetValueOrDefault(venueId)
            : null;
        foreach (var source in show.Sources)
        {
            source.ImportedVenue = source.venue_id is { } sourceVenue
                ? venues.GetValueOrDefault(sourceVenue)
                : null;
        }
        show.Year = await session.Connection.QuerySingleOrDefaultAsync<CatalogYear>(
            session.Command(
                "SELECT y.*,a.uuid artist_uuid FROM years y JOIN shows s ON s.year_id=y.id JOIN artists a ON a.id=y.artist_id WHERE s.uuid=@uuid",
                new { show.uuid },
                cancellationToken
            )
        );
        show.Tour = await session.Connection.QuerySingleOrDefaultAsync<CatalogTour>(
            session.Command(
                """
                SELECT t.uuid, a.uuid artist_uuid, t.start_date::timestamp start_date,
                       t.end_date::timestamp end_date, t.name, t.slug, t.upstream_identifier,
                       t.created_at,t.updated_at
                FROM tours t JOIN shows s ON s.tour_id=t.id JOIN artists a ON a.id=t.artist_id
                WHERE s.uuid=@uuid
                """,
                new { show.uuid },
                cancellationToken
            )
        );
        show.Era = await session.Connection.QuerySingleOrDefaultAsync<CatalogEra>(
            session.Command(
                "SELECT e.\"order\",e.name FROM eras e JOIN shows s ON s.era_id=e.id WHERE s.uuid=@uuid",
                new { show.uuid },
                cancellationToken
            )
        );
        show.VenueDisplayName = show.ImportedVenue?.name;
        if (!show.normalization_enabled)
        {
            return;
        }

        var chain = (
            await session.Connection.QueryAsync<CatalogVenue>(
                session.Command(
                    """
                    WITH RECURSIVE chain AS (
                      SELECT v.*, ARRAY[v.id] path FROM norm.venues v JOIN norm.show_venues sv ON sv.venue_id=v.id WHERE sv.show_uuid=@uuid
                      UNION ALL
                      SELECT v.*, c.path || v.id FROM norm.venues v JOIN chain c ON v.id=c.merged_into
                      WHERE cardinality(c.path)<32 AND NOT v.id=ANY(c.path)
                    ) SELECT * FROM chain
                    """,
                    new { show.uuid },
                    cancellationToken
                )
            )
        ).ToArray();
        var canonical = chain.SingleOrDefault(x => x.merged_into is null);
        if (canonical is null)
        {
            if (chain.Length > 0)
            {
                logger.LogWarning(
                    "Invalid normalized venue merge chain for show {ShowUuid}",
                    show.uuid
                );
            }
            return;
        }
        show.NormalizedVenue = canonical;
        show.VenueDisplayName = await session.Connection.QuerySingleAsync<string>(
            session.Command(
                "SELECT norm.venue_name_on(@id,@date::date)",
                new { canonical.id, show.date },
                cancellationToken
            )
        );
    }

    private async Task ApplyNormalization(
        CatalogReadSession session,
        IReadOnlyList<CatalogTrack> tracks,
        bool approved,
        CancellationToken cancellationToken
    )
    {
        if (!approved)
        {
            foreach (var track in tracks)
            {
                track.Normalization = new("unavailable", "disabled", []);
            }
            return;
        }
        if (tracks.Count == 0)
        {
            return;
        }

        var uuids = tracks.Select(x => x.uuid).ToArray();
        var parts = (
            await session.Connection.QueryAsync<CatalogPart>(
                session.Command(
                    "SELECT * FROM norm.track_songs WHERE track_uuid=ANY(@uuids) ORDER BY track_uuid,part",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();
        var songs = (
            await session.Connection.QueryAsync<CatalogSong>(
                session.Command(
                    """
                    WITH RECURSIVE chain AS (
                      SELECT s.*,ARRAY[s.id] path FROM norm.songs s WHERE s.id IN (SELECT song_id FROM norm.track_songs WHERE track_uuid=ANY(@uuids))
                      UNION ALL
                      SELECT s.*,c.path || s.id FROM norm.songs s JOIN chain c ON s.id=c.merged_into
                      WHERE cardinality(c.path)<32 AND NOT s.id=ANY(c.path)
                    ) SELECT DISTINCT id,artist_uuid,name,kind,merged_into FROM chain
                    """,
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.id);
        var partsByTrack = parts.ToLookup(x => x.track_uuid);
        foreach (var track in tracks)
        {
            var interpretation = partsByTrack[track.uuid].ToArray();
            var consistent = true;
            foreach (var part in interpretation)
            {
                if (!part.resolved || part.song_id is null)
                {
                    continue;
                }
                part.Song = ResolveSong(part.song_id, track.artist_uuid, songs);
                consistent &= part.Song is not null;
            }
            if (!consistent)
            {
                logger.LogWarning("Invalid normalized song link for track {TrackUuid}", track.uuid);
                track.Normalization = new("unavailable", "inconsistent", []);
            }
            else if (interpretation.Length > 0)
            {
                track.Normalization = new(
                    interpretation.Any(x => !x.resolved) ? "partial" : "interpreted",
                    null,
                    interpretation
                );
            }
        }
    }

    private static CatalogSong? ResolveSong(
        string id,
        Guid artist,
        IReadOnlyDictionary<string, CatalogSong> songs
    )
    {
        var seen = new HashSet<string>();
        while (
            seen.Count < 32
            && seen.Add(id)
            && songs.TryGetValue(id, out var song)
            && song.artist_uuid == artist
        )
        {
            if (song.merged_into is null)
            {
                return song;
            }
            id = song.merged_into;
        }
        return null;
    }

    private const string TrackSelect = """
        SELECT t.*, src.uuid source_uuid, sets.uuid source_set_uuid, sh.uuid show_uuid, a.uuid artist_uuid
        FROM source_tracks t JOIN sources src ON src.id=t.source_id
        JOIN source_sets sets ON sets.id=t.source_set_id AND sets.source_id=src.id
        JOIN shows sh ON sh.id=src.show_id JOIN artists a ON a.id=src.artist_id
        """;
}
