using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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

		public async Task<IEnumerable<SourceTrack>> InsertAll(IEnumerable<SourceTrack> songs)
        {
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SourceTrack>();

                foreach (var song in songs)
                {
                    var p = new {
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
                        RETURNING *
                    ", p));
                }

                return inserted;
            });
        }
    }
}