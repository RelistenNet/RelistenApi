using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Relisten.Data
{
    public class EnittyOneToManyMapper<TP, TC, TPk>
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
            var ReviewMapper = new EnittyOneToManyMapper<SourceFull, SourceReview, int>()
            {
                AddChildAction = (source, review) =>
                {
                    if (source.reviews == null)
                        source.reviews = new List<SourceReview>();

                    if (review != null)
                    {
                        source.reviews.Add(review);
                    }
                },
                ParentKey = (source) => source.id
            };

            var TrackMapper = new EnittyOneToManyMapper<SourceSet, SourceTrack, int>()
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
            
            var t_srcsWithReviews = db.WithConnection(con => con.QueryAsync<SourceFull, SourceReview, SourceFull>(@"
                SELECT
                    s.*, r.*
                FROM
                    sources s
                    LEFT JOIN source_reviews r ON r.source_id = s.id
                WHERE
                    s.artist_id = @artistId
                    AND s.show_id = @showId
                ",
                ReviewMapper.Map,
                new { showId = show.id, artistId = artist.id })
            );

            var t_setsWithTracks = db.WithConnection(con => con.QueryAsync<SourceSet, SourceTrack, SourceSet>(@"
                SELECT
                    s.*, t.*
                FROM
                    source_sets s
                    LEFT JOIN sources src ON src.id = s.source_id
                    LEFT JOIN source_tracks t ON t.source_set_id = s.id
                WHERE
                    src.show_id = @showId
                ",
                TrackMapper.Map,
                new { showId = show.id })
            );

            await Task.WhenAll(t_srcsWithReviews, t_setsWithTracks);

            var srcsWithReviews = t_srcsWithReviews.Result
                .Where(s => s != null)
                ;

            var setsWithTracks = t_setsWithTracks.Result
                .Where(s => s != null)
                .GroupBy(s => s.source_id)
                .ToDictionary(grp => grp.Key, grp => grp.AsList())
                ;

            foreach (var src in srcsWithReviews)
            {
                src.sets = setsWithTracks[src.id];
            }

            return srcsWithReviews;
        }

        public async Task<IEnumerable<Source>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Source>(@"
                SELECT
                    *
                FROM
                    sources
                WHERE
                    artist_id = @id
            ", artist));
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
                ", source);

                cnt += await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        source_tracks
                    WHERE
                        source_id = @id
                ", source);

                return cnt;
            });
        }

        public async Task<Source> Save(Source source)
        {
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
                        num_ratings = @num_ratings
                    WHERE
                        id = @id
                    RETURNING *
                ", source));
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
                            num_ratings
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
                            @num_ratings
                        )
                    RETURNING *
                ", source));
            }
        }
    }
}