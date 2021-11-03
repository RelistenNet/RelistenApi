using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class SourceTrackPlaysService : RelistenDataServiceBase
    {
        public SourceTrackPlaysService(DbService db) : base(db)
        {
        }

        public async Task<SourceTrackPlay> RecordPlayedTrack(SourceTrackPlay track)
        {
            return await db.WithConnection(con => con.QuerySingleAsync<SourceTrackPlay>(@"
				INSERT INTO
					source_track_plays

					(
						source_track_uuid,
						user_uuid,
						app_type
					)
				VALUES
					(
						@source_track_uuid,
						@user_uuid,
						@app_type
					)

				RETURNING *
			", track));
        }

        public async Task<IEnumerable<SourceTrackPlay>> PlayedTracksSince(int? lastSeenId = null, int limit = 2000)
        {
            var tracks = await db.WithConnection(con => con.QueryAsync<SourceTrackPlay>($@"
				SELECT
					t.*
				FROM
					source_track_plays t
				WHERE
					1=1
					{(lastSeenId != null ? "AND t.id > @lastSeenId" : "")}
				ORDER BY
					t.id {(lastSeenId != null ? "" : "DESC")}
				LIMIT
					@limit
			", new {lastSeenId, limit}));

            return tracks.OrderBy(t => t.id);
        }
    }
}
