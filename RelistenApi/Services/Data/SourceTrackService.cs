using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class SourceTrackService : RelistenDataServiceBase
    {
        public SourceTrackService(DbService db) : base(db) { }

        public async Task<SourceTrack?> ForId(int id)
        {
            return (await ForIds(new[] {id})).FirstOrDefault();
        }

        public Task<IEnumerable<SourceTrack>> ForIds(IList<int> ids)
        {
            return db.WithConnection(con => con.QueryAsync<SourceTrack>(@"
                SELECT
                    t.*
                FROM
                    source_tracks t
                WHERE
                    t.id = ANY(@trackIds)
            ", new {trackIds = ids}));
        }

        public async Task<SourceTrack?> ForUUID(Guid uuid)
        {
            return (await ForUUIDs(new[] {uuid})).FirstOrDefault();
        }

        public Task<IEnumerable<SourceTrack>> ForUUIDs(IList<Guid> uuids)
        {
            return db.WithConnection(con => con.QueryAsync<SourceTrack>(@"
                SELECT
                    t.*
                FROM
                    source_tracks t
                WHERE
                    t.uuid = ANY(@trackUUIDs)
            ", new {trackUUIDs = uuids}));
        }

        public async Task<IEnumerable<SourceTrack>> InsertAll(Source source, IEnumerable<SourceTrack> tracks)
        {
            var trackList = tracks.ToList();
            if (trackList.Count == 0)
            {
                return Enumerable.Empty<SourceTrack>();
            }

            return await db.WithWriteConnection(async con =>
            {
                // Batch insert using UNNEST for all tracks at once
                var inserted = (await con.QueryAsync<SourceTrack>(@"
                    INSERT INTO
                        source_tracks
                        (
                            source_id,
                            source_set_id,
                            track_position,
                            duration,
                            title,
                            slug,
                            mp3_url,
                            mp3_md5,
                            flac_url,
                            flac_md5,
                            updated_at,
                            artist_id,
                            uuid
                        )
                    SELECT
                        source_id,
                        source_set_id,
                        track_position,
                        duration,
                        title,
                        slug,
                        mp3_url,
                        mp3_md5,
                        flac_url,
                        flac_md5,
                        updated_at,
                        artist_id,
                        md5(artist_id || '::track::' || mp3_url)::uuid
                    FROM UNNEST(
                        @source_ids::int[],
                        @source_set_ids::int[],
                        @track_positions::int[],
                        @durations::int[],
                        @titles::text[],
                        @slugs::text[],
                        @mp3_urls::text[],
                        @mp3_md5s::text[],
                        @flac_urls::text[],
                        @flac_md5s::text[],
                        @updated_ats::timestamp with time zone[],
                        @artist_ids::int[]
                    ) AS t(source_id, source_set_id, track_position, duration, title, slug, mp3_url, mp3_md5, flac_url, flac_md5, updated_at, artist_id)
                    ON CONFLICT ON CONSTRAINT source_tracks_uuid_key
                    DO
                        UPDATE SET
                            source_id = EXCLUDED.source_id,
                            source_set_id = EXCLUDED.source_set_id,
                            track_position = EXCLUDED.track_position,
                            duration = EXCLUDED.duration,
                            title = EXCLUDED.title,
                            slug = EXCLUDED.slug,
                            mp3_url = EXCLUDED.mp3_url,
                            mp3_md5 = EXCLUDED.mp3_md5,
                            flac_url = EXCLUDED.flac_url,
                            flac_md5 = EXCLUDED.flac_md5,
                            updated_at = EXCLUDED.updated_at,
                            artist_id = EXCLUDED.artist_id
                    RETURNING *
                ", new
                {
                    source_ids = trackList.Select(t => t.source_id).ToArray(),
                    source_set_ids = trackList.Select(t => t.source_set_id).ToArray(),
                    track_positions = trackList.Select(t => t.track_position).ToArray(),
                    durations = trackList.Select(t => t.duration).ToArray(),
                    titles = trackList.Select(t => t.title).ToArray(),
                    slugs = trackList.Select(t => t.slug).ToArray(),
                    mp3_urls = trackList.Select(t => t.mp3_url).ToArray(),
                    mp3_md5s = trackList.Select(t => t.mp3_md5).ToArray(),
                    flac_urls = trackList.Select(t => t.flac_url).ToArray(),
                    flac_md5s = trackList.Select(t => t.flac_md5).ToArray(),
                    updated_ats = trackList.Select(t => t.updated_at).ToArray(),
                    artist_ids = trackList.Select(t => t.artist_id).ToArray()
                })).ToList();

                // mark these as orphaned
                await con.ExecuteAsync(@"
                    UPDATE
                        source_tracks
                    SET
                        is_orphaned = TRUE
                    WHERE
                        source_id = @sourceId
                        AND NOT(mp3_url = ANY(@mp3Urls))
                ", new {sourceId = source.id, mp3Urls = trackList.Select(t => t.mp3_url).ToList()});

                return inserted;
            });
        }
    }
}
