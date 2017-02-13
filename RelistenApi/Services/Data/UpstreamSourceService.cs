using System;
using System.Collections.Generic;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;

namespace Relisten.Data
{
	public class UpstreamSourceService : RelistenDataServiceBase
	{
		public UpstreamSourceService(DbService db) : base(db)
		{
		}

		public async Task<IEnumerable<UpstreamSource>> AllUpstreamSources()
		{
			return await db.WithConnection(conn => conn.QueryAsync<UpstreamSource>(@"
				SELECT
					*
				FROM
					upstream_sources
			"));
		}
	}
}
