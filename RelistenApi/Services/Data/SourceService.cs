using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class EntityOneToManyMapper<TP, TC, TPk>
    {
        private readonly IDictionary<TPk, TP> _lookup = new Dictionary<TPk, TP>();

        public Action<TP, TC> AddChildAction { get; set; }

        public Func<TP, TPk> ParentKey { get; set; }


        public virtual TP Map(TP parent, TC child)
        {
            TP entity;
            var found = true;
            var primaryKey = ParentKey(parent);

            if (!_lookup.TryGetValue(primaryKey, out entity))
            {
                _lookup.Add(primaryKey, parent);
                entity = parent;
                found = false;
            }

            AddChildAction(entity, child);

            return !found ? entity : default;
        }
    }

    public class SourceService : RelistenDataServiceBase
    {
        public SourceService(
            DbService db,
            SourceSetService sourceSetService
        ) : base(db)
        {
            _sourceSetService = sourceSetService;
        }

        private SourceSetService _sourceSetService { get; }

        public async Task<Source> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Source>(@"
                SELECT
                    src.*
                FROM
                    sources src
                WHERE
                    src.artist_id = @artistId
                    AND src.upstream_identifier = @upstreamId
            ", new {artistId = artist.id, upstreamId}));
        }

        public Task<IEnumerable<SourceFull>> FullSourcesForShow(Artist artist, Show show)
        {
            return FullSourcesGeneric(
                "s.artist_id = @artistId AND s.show_id = @showId",
                new {showId = show.id, artistId = artist.id}
            );
        }

        public async Task<IEnumerable<SourceFull>> FullSourcesGeneric(string where, object param)
        {
            var LinkMapper = new EntityOneToManyMapper<SourceFull, Link, int>
            {
                AddChildAction = (source, link) =>
                {
                    if (source.links == null)
                    {
                        source.links = new List<Link>();
                    }

                    if (link != null)
                    {
                        source.links.Add(link);
                    }
                },
                ParentKey = source => source.id
            };

            var TrackMapper = new EntityOneToManyMapper<SourceSet, SourceTrack, int>
            {
                AddChildAction = (set, track) =>
                {
                    if (set.tracks == null)
                    {
                        set.tracks = new List<SourceTrack>();
                    }

                    if (track != null)
                    {
                        set.tracks.Add(track);
                    }
                },
                ParentKey = set => set.id
            };

            return await db.WithConnection(async con =>
            {
                var t_srcsWithReviews = await con.QueryAsync<SourceFull, Link, SourceFull>($@"
	                SELECT
	                    s.*
						, a.uuid as artist_uuid
						, v.uuid as venue_uuid
                        , sh.uuid as show_uuid
						, COALESCE(review_counts.source_review_count, 0) as review_count
						, l.*
	                FROM
	                    sources s
						JOIN artists a ON a.id = s.artist_id
						LEFT JOIN venues v ON v.id = s.venue_id
						LEFT JOIN shows sh ON sh.id = s.show_id
						LEFT JOIN links l ON l.source_id = s.id

						LEFT JOIN source_review_counts review_counts ON review_counts.source_id = s.id
	                WHERE
	                    {where}
                    ORDER BY
                        s.avg_rating_weighted DESC
	            ",
                    LinkMapper.Map,
                    param
                );

                var t_setsWithTracks = await con.QueryAsync<SourceSet, SourceTrack, SourceSet>($@"
	                SELECT
	                    sets.*, a.uuid as artist_uuid, s.uuid as source_uuid
                        , t.*, s.uuid as source_uuid, sets.uuid as source_set_uuid, a.uuid as artist_uuid
	                FROM
	                    source_sets sets
	                    LEFT JOIN sources s ON s.id = sets.source_id
						LEFT JOIN artists a ON a.id = s.artist_id
	                    LEFT JOIN source_tracks t ON t.source_set_id = sets.id
	                WHERE
	                    {where}
					ORDER BY
						sets.index ASC, t.track_position ASC
	            ",
                    TrackMapper.Map,
                    param
                );

                var setsWithTracks = t_setsWithTracks
                        .Where(s => s != null)
                        .GroupBy(s => s.source_id)
                        .ToDictionary(grp => grp.Key, grp => grp.AsList())
                    ;

                var sources = t_srcsWithReviews
                    .Where(s => s != null)
                    .ToList();

                foreach (var src in sources)
                {
                    src.sets = setsWithTracks[src.id];
                }

                return sources;
            });
        }

        public Task<IEnumerable<SourceReview>> ReviewsForSource(int sourceId)
        {
            return db.WithConnection(conn => conn.QueryAsync<SourceReview>(@"
				SELECT
					r.*
				FROM
					source_reviews r
				WHERE
					r.source_id = @sourceId
				ORDER BY
					r.updated_at DESC
			", new {sourceId}));
        }

        public Task<IEnumerable<SlimSourceWithShowVenueAndArtist>> SlimSourceWithShowAndArtistForIds(IList<int> ids)
        {
            return db.WithConnection(con =>
                con.QueryAsync<SlimSourceWithShowVenueAndArtist, SlimArtistWithFeatures, Features, Show,
                    VenueWithShowCount, Venue, SlimSourceWithShowVenueAndArtist>(@"
				SELECT
					s.*, a.*, f.*, sh.*, shVenue.*, shVenue_counts.shows_at_venue, sVenue.*
				FROM
					sources s
					JOIN artists a ON s.artist_id = a.id
					JOIN features f ON f.artist_id = a.id
					JOIN shows sh ON sh.id = s.show_id
                    LEFT JOIN venues shVenue ON shVenue.id = sh.venue_id
                    LEFT JOIN venues sVenue ON sVenue.id = sh.venue_id
                    LEFT JOIN venue_show_counts shVenue_counts ON shVenue_counts.id = sh.venue_id
				WHERE
					s.id = ANY(@ids)
			", (s, a, f, sh, shVenue, sVenue) =>
                {
                    sh.venue = shVenue;
                    s.venue = sVenue;

                    a.features = f;

                    s.artist = a;
                    s.show = sh;

                    return s;
                }, new {ids}));
        }

        public async Task<IEnumerable<Source>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Source>(@"
                SELECT
                    s.*
                FROM
                    sources s
                WHERE
                    s.artist_id = @id
            ", new {artist.id}));
        }

        public async Task<IEnumerable<SourceReviewInformation>> AllSourceReviewInformationForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<SourceReviewInformation>(@"
                SELECT
                    r.source_id
                    , s.upstream_identifier
                    , r.source_review_max_updated_at as review_max_updated_at
                    , r.source_review_count as review_count
                FROM
                    sources s
                    JOIN source_review_counts r ON s.id = r.source_id
                WHERE
                    s.artist_id = @id
            ", new {artist.id}));
        }

        public async Task<int> RemoveSourcesWithUpstreamIdentifiers(IEnumerable<string> upstreamIdentifiers)
        {
            return await db.WithConnection(con => con.ExecuteAsync(@"
                DELETE FROM
                    sources
                WHERE
                    upstream_identifier = ANY(@upstreamIdentifiers)
            ", new {upstreamIdentifiers = upstreamIdentifiers.ToList()}));
        }

        public async Task<Source> Save(Source source)
        {
            var p = new
            {
                source.id,
                source.artist_id,
                source.show_id,
                source.is_soundboard,
                source.is_remaster,
                source.avg_rating,
                source.num_reviews,
                source.upstream_identifier,
                source.has_jamcharts,
                source.description,
                source.taper_notes,
                source.source,
                source.taper,
                source.transferrer,
                source.lineage,
                source.updated_at,
                source.display_date,
                source.venue_id,
                source.num_ratings,
                source.flac_type
            };

            return await db.WithConnection(con => con.QuerySingleAsync<Source>(@"
                INSERT INTO
                    sources
                    (
                        artist_id,
                        show_id,
                        is_soundboard,
                        is_remaster,
                        avg_rating,
                        num_reviews,
                        upstream_identifier,
                        has_jamcharts,
                        description,
                        taper_notes,
                        source,
                        taper,
                        transferrer,
                        lineage,
                        updated_at,
                        display_date,
                        venue_id,
                        num_ratings,
                        flac_type,
                        uuid
                    )
                VALUES
                    (
                        @artist_id,
                        @show_id,
                        @is_soundboard,
                        @is_remaster,
                        @avg_rating,
                        @num_reviews,
                        @upstream_identifier,
                        @has_jamcharts,
                        @description,
                        @taper_notes,
                        @source,
                        @taper,
                        @transferrer,
                        @lineage,
                        @updated_at,
                        @display_date,
                        @venue_id,
                        @num_ratings,
                        @flac_type,
                        md5(@artist_id || '::source::' || @upstream_identifier)::uuid
                    )
                ON CONFLICT ON CONSTRAINT sources_uuid_key
                DO
                    UPDATE SET
                        artist_id = EXCLUDED.artist_id,
                        show_id = EXCLUDED.show_id,
                        is_soundboard = EXCLUDED.is_soundboard,
                        is_remaster = EXCLUDED.is_remaster,
                        avg_rating = EXCLUDED.avg_rating,
                        num_reviews = EXCLUDED.num_reviews,
                        upstream_identifier = EXCLUDED.upstream_identifier,
                        has_jamcharts = EXCLUDED.has_jamcharts,
                        description = EXCLUDED.description,
                        taper_notes = EXCLUDED.taper_notes,
                        source = EXCLUDED.source,
                        taper = EXCLUDED.taper,
                        transferrer = EXCLUDED.transferrer,
                        lineage = EXCLUDED.lineage,
                        updated_at = EXCLUDED.updated_at,
                        display_date = EXCLUDED.display_date,
                        venue_id = EXCLUDED.venue_id,
                        num_ratings = EXCLUDED.num_ratings,
                        flac_type = EXCLUDED.flac_type

                RETURNING *
            ", p));
        }
    }
}
