using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;

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

			return !found ? entity : default(TP);
		}
	}

    public class SourceService : RelistenDataServiceBase
    {
        private SourceSetService _sourceSetService { get; set; }

        public SourceService(
            DbService db,
            SourceSetService sourceSetService
        ) : base(db)
        {
            _sourceSetService = sourceSetService;
        }

        public async Task<Source> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Source>(@"
                SELECT
                    *
                FROM
                    sources
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstreamId
            ", new { artistId = artist.id, upstreamId }));
        }

        public async Task<IEnumerable<SourceFull>> FullSourcesForShow(Artist artist, Show show)
        {
            var LinkMapper = new EntityOneToManyMapper<SourceFull, Link, int>()
            {
                AddChildAction = (source, link) =>
                {
                    if (source.links == null)
                        source.links = new List<Link>();

                    if (link != null)
                    {
                        source.links.Add(link);
                    }
                },
                ParentKey = (source) => source.id
            };

            var TrackMapper = new EntityOneToManyMapper<SourceSet, SourceTrack, int>()
            {
                AddChildAction = (set, track) =>
                {
                    if (set.tracks == null)
                        set.tracks = new List<SourceTrack>();

                    if (track != null)
                    {
                        set.tracks.Add(track);
                    }
                },
                ParentKey = (set) => set.id
            };

			return await db.WithConnection(async con => {
				var t_srcsWithReviews = await con.QueryAsync<SourceFull, Link, SourceFull>(@"
	                SELECT
	                    s.*
						, COALESCE(review_counts.source_review_count, 0) as review_count
						, l.*
	                FROM
	                    sources s
						LEFT JOIN links l ON l.source_id = s.id

						LEFT JOIN source_review_counts review_counts ON review_counts.source_id = s.id
	                WHERE
	                    s.artist_id = @artistId
	                    AND s.show_id = @showId
                    ORDER BY
                        s.avg_rating_weighted DESC
	            ",
					LinkMapper.Map,
					new { showId = show.id, artistId = artist.id }
				);

				var t_setsWithTracks = await con.QueryAsync<SourceSet, SourceTrack, SourceSet>(@"
	                SELECT
	                    s.*, t.*
	                FROM
	                    source_sets s
	                    LEFT JOIN sources src ON src.id = s.source_id
	                    LEFT JOIN source_tracks t ON t.source_set_id = s.id
	                WHERE
	                    src.show_id = @showId
					ORDER BY
						s.index ASC, t.track_position ASC
	            ",
					TrackMapper.Map,
					new { showId = show.id }
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
			", new { sourceId }));
	    }

		public Task<IEnumerable<SlimSourceWithShowVenueAndArtist>> SlimSourceWithShowAndArtistForIds(IList<int> ids)
		{
			return db.WithConnection(con => con.QueryAsync<SlimSourceWithShowVenueAndArtist, SlimArtistWithFeatures, Features, Show, VenueWithShowCount, Venue, SlimSourceWithShowVenueAndArtist>($@"
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
			}, new { ids }));		
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
            ", new { artist.id }));
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
            ", new { artist.id }));
        }

        public async Task<int> DropAllSetsAndTracksForSource(Source source)
        {
            return await db.WithConnection(async con =>
            {
                var cnt = await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        source_sets
                    WHERE
                        source_id = @id
                ", new { source.id });

                cnt += await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        source_tracks
                    WHERE
                        source_id = @id
                ", new { source.id });

                return cnt;
            });
        }

        public async Task<Source> Save(Source source)
        {
            var p = new {
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

            if (source.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Source>(@"
                    UPDATE
                        sources
                    SET
                        artist_id = @artist_id,
                        show_id = @show_id,
                        is_soundboard = @is_soundboard,
                        is_remaster = @is_remaster,
                        avg_rating = @avg_rating,
                        num_reviews = @num_reviews,
                        upstream_identifier = @upstream_identifier,
                        has_jamcharts = @has_jamcharts,
                        description = @description,
                        taper_notes = @taper_notes,
                        source = @source,
                        taper = @taper,
                        transferrer = @transferrer,
                        lineage = @lineage,
                        updated_at = @updated_at,
                        display_date = @display_date,
                        venue_id = @venue_id,
                        num_ratings = @num_ratings,
						flac_type = @flac_type,
                        uuid = md5(@artist_id || '::source::' || @upstream_identifier)::uuid
                    WHERE
                        id = @id
                    RETURNING *
                ", p));
            }
            else
            {
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
                    RETURNING *
                ", p));
            }
        }
    }
}