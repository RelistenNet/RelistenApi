using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class SourceSetService : RelistenDataServiceBase
    {
        public SourceSetService(DbService db) : base(db) { }

        public async Task<IEnumerable<SourceSet>> AllForSources(IEnumerable<int> source_ids)
        {
            return await db.WithConnection(con => con.QueryAsync<SourceSet>(@"
                SELECT
                    r.*
                FROM
                    source_sets r
                WHERE
                    source_id = ANY(@source_ids)
            ", new { source_ids }));
        }

        public async Task<SourceSet> Insert(SourceSet set)
        {
            var l = new List<SourceSet>();
            l.Add(set);

            return (await InsertAll(l)).FirstOrDefault(); 
        }

        public async Task<IEnumerable<SourceSet>> InsertAll(IEnumerable<SourceSet> sets)
        {
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SourceSet>();

                foreach (var set in sets)
                {
                    var p = new {
                        set.id,
                        set.source_id,
                        set.index,
                        set.is_encore,
                        set.name,
                        set.updated_at,
                    };

                    inserted.Add(await con.QuerySingleAsync<SourceSet>(@"
                        INSERT INTO
                            source_sets

                            (
                                source_id,
                                index,
                                is_encore,
                                name,
                                updated_at
                            )
                        VALUES
                            (
                                @source_id,
                                @index,
                                @is_encore,
                                @name,
                                @updated_at
                            )
                        RETURNING *
                    ", p));
                }

                return inserted;
            });
        }
    }
}