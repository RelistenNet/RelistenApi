using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Relisten.Data
{
    public class SourceTrackService : RelistenDataServiceBase
    {
        public SourceTrackService(DbService db) : base(db) { }

		public async Task<SourceTrack> ForId(int id)
		{
			return (await ForIds(new[] { id })).FirstOrDefault();
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
            ", new { trackIds = ids }));
		}

		public async Task<SourceTrack> ForUUID(Guid uuid)
		{
			return (await ForUUIDs(new[] { uuid })).FirstOrDefault();
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
            ", new { trackUUIDs = uuids }));
		}

		public async Task<IEnumerable<SourceTrack>> InsertAll(Source source, IEnumerable<SourceTrack> tracks)
        {
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SourceTrack>();

                foreach (var song in tracks)
                {
                    var p = new {
                        song.id,
                        song.source_id,
                        song.source_set_id,
                        song.track_position,
                        song.duration,
                        song.title,
                        song.slug,
                        song.mp3_url,
                        song.mp3_md5,
                        song.flac_url,
                        song.flac_md5,
                        song.updated_at,
                        song.artist_id,
                    };

                    inserted.Add(await con.QuerySingleAsync<SourceTrack>(@"
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
                        VALUES
                            (
                                @source_id,
                                @source_set_id,
                                @track_position,
                                @duration,
                                @title,
                                @slug,
                                @mp3_url,
                                @mp3_md5,
                                @flac_url,
                                @flac_md5,
                                @updated_at,
                                @artist_id,
                                md5(@artist_id || '::track::' || @mp3_url)::uuid
                            )
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
                    ", p));
                }

                // mark these as orphaned
                await con.ExecuteAsync(@"
                    UPDATE
                        source_tracks
                    SET
                        is_orphaned = TRUE
                    WHERE
                        source_id = @sourceId
                        AND NOT(mp3_url = ANY(@mp3Urls))
                ", new {
                    sourceId = source.id,
                    mp3Urls = tracks.Select(t => t.mp3_url).ToList()
                });

                return inserted;
            });
        }
    }
}