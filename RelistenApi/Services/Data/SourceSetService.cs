using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

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
            ", new {source_ids}));
        }

        public async Task<SourceSet?> Update(Source source, SourceSet set)
        {
            var l = new List<SourceSet>();
            l.Add(set);

            return (await UpdateAll(source, l)).FirstOrDefault();
        }

        public async Task<IEnumerable<SourceSet>> UpdateAll(Source source, IEnumerable<SourceSet> sets)
        {
            return await db.WithWriteConnection(async con =>
            {
                var inserted = new List<SourceSet>();

                foreach (var set in sets)
                {
                    var p = new
                    {
                        set.id,
                        set.source_id,
                        set.index,
                        set.is_encore,
                        set.name,
                        set.updated_at,
                        sourceUuid = source.uuid
                    };

                    inserted.Add(await con.QuerySingleAsync<SourceSet>(@"
                        INSERT INTO
                            source_sets

                            (
                                source_id,
                                index,
                                is_encore,
                                name,
                                updated_at,
                                uuid
                            )
                        VALUES
                            (
                                @source_id,
                                @index,
                                @is_encore,
                                @name,
                                @updated_at,
                                md5(@sourceUuid || '::source_set::' || @index)::uuid
                            )
                        ON CONFLICT ON CONSTRAINT source_sets_source_id_index_key
                        DO
                            UPDATE SET
                                is_encore = EXCLUDED.is_encore,
                                name = EXCLUDED.name,
                                updated_at = EXCLUDED.updated_at

                        RETURNING *
                    ", p));
                }

                await con.ExecuteAsync(@"
                    DELETE FROM
                        source_sets
                    WHERE
                        source_id = @sourceId
                        AND NOT(index = ANY(@indicies))
                ", new {sourceId = source.id, indicies = sets.Select(s => s.index).ToList()});

                return inserted;
            });
        }
    }
}
