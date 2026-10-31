using System.Linq;
using Relisten.Api.Models;

namespace Relisten.Catalog.Compatibility;

/// <summary>
/// Preserves imported v2/v3 recording fields. Existing serializers choose numeric
/// versus UUID identities; normalization stays on the native v4 resources.
/// </summary>
internal static class LegacyRecordingProjection
{
    public static SourceFull Source(CatalogSource source) =>
        new()
        {
            id = checked((int)source.id),
            uuid = source.uuid,
            artist_id = checked((int)source.artist_id),
            artist_uuid = source.artist_uuid,
            show_id = (int?)source.show_id,
            show_uuid = source.show_uuid,
            venue_id = (int?)source.venue_id,
            venue_uuid = source.venue_uuid,
            display_date = source.display_date,
            is_soundboard = source.is_soundboard,
            is_remaster = source.is_remaster,
            has_jamcharts = source.has_jamcharts,
            avg_rating = source.avg_rating,
            num_reviews = source.num_reviews,
            num_ratings = source.num_ratings,
            avg_rating_weighted = source.avg_rating_weighted,
            duration = source.duration,
            upstream_identifier = source.upstream_identifier,
            description = source.description!,
            taper_notes = source.taper_notes!,
            source = source.source!,
            taper = source.taper!,
            transferrer = source.transferrer!,
            lineage = source.lineage!,
            flac_type = (FlacType)source.flac_type,
            review_count = source.review_count,
            created_at = source.created_at,
            updated_at = source.updated_at,
            // Old detail includes playable sets only. Native reads also retain empty sets.
            sets = source.Sets.Where(set => set.Tracks.Count > 0).Select(Set).ToArray(),
            links = source.Links.Select(Link).ToArray(),
        };

    private static SourceSet Set(CatalogSet set) =>
        new()
        {
            id = checked((int)set.id),
            uuid = set.uuid,
            source_id = checked((int)set.source_id),
            source_uuid = set.source_uuid,
            artist_id = checked((int)set.artist_id),
            artist_uuid = set.artist_uuid,
            index = set.index,
            name = set.name,
            is_encore = set.is_encore,
            created_at = set.created_at,
            updated_at = set.updated_at,
            tracks = set.Tracks.Select(Track).ToArray(),
        };

    private static SourceTrack Track(CatalogTrack track) =>
        new()
        {
            id = checked((int)track.id),
            uuid = track.uuid,
            source_id = checked((int)track.source_id),
            source_uuid = track.source_uuid,
            source_set_id = checked((int)track.source_set_id),
            source_set_uuid = track.source_set_uuid,
            show_uuid = track.show_uuid,
            artist_id = checked((int)track.artist_id),
            artist_uuid = track.artist_uuid,
            track_position = track.track_position,
            duration = track.duration,
            title = track.title,
            slug = track.slug,
            mp3_url = track.mp3_url,
            mp3_md5 = track.mp3_md5,
            flac_url = track.flac_url,
            flac_md5 = track.flac_md5,
            created_at = track.created_at,
            updated_at = track.updated_at,
        };

    private static Link Link(CatalogLink link) =>
        new()
        {
            id = checked((int)link.id),
            source_id = checked((int)link.source_id),
            upstream_source_id = checked((int)link.upstream_source_id),
            for_reviews = link.for_reviews,
            for_ratings = link.for_ratings,
            for_source = link.for_source,
            label = link.label,
            url = link.url,
            created_at = link.created_at,
            updated_at = link.updated_at,
        };
}
