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

        public async Task<IEnumerable<SourceTrack>> InsertAll(IEnumerable<SourceTrack> songs)
        {
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SourceTrack>();

                foreach (var song in songs)
                {
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
                                md5,
                                updated_at
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
                                @md5,
                                @updated_at
                            )
                        RETURNING *
                    ", song));
                }

                return inserted;
            });
        }
    }
}