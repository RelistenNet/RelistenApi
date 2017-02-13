using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;
using Relisten.Import;

namespace Relisten.Data
{
	public class ArtistService : RelistenDataServiceBase
	{
		public ImporterService _importService { get; set; }

		public ArtistService(DbService db, ImporterService importService) : base(db)
		{
			_importService = importService;
		}

		private static Func<Artist, Features, Artist> joiner = (Artist artist, Features features) =>
			{
				artist.features = features;
				return artist;
			};

		public async Task<IEnumerable<ArtistWithCounts>> AllWithCounts()
		{
			return await FillInUpstreamSources(await db.WithConnection(con => con.QueryAsync(@"
                WITH show_counts AS (
                    SELECT
                        artist_id,
                        COUNT(*) as show_count
                    FROM
                        shows
                    GROUP BY
                        artist_id
                ), source_counts AS (
                    SELECT
                        artist_id,
                        COUNT(*) as source_count
                    FROM
                        sources
                    GROUP BY
                        artist_id	
                )

                SELECT
                    a.*, show_count, source_count, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
                    LEFT JOIN show_counts sh ON sh.artist_id = a.id
                    LEFT JOIN source_counts src ON src.artist_id = a.id
				ORDER BY
					a.featured, a.name
            ", (ArtistWithCounts artist, Features features) =>
			{
				artist.features = features;
				return artist;
			})));
		}

		public async Task<IEnumerable<Artist>> All()
		{
			return await FillInUpstreamSources(await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
				ORDER BY
					a.featured, a.name
            ", joiner)));
		}

		public async Task<Artist> FindArtistById(int id)
		{
			var a = await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
				WHERE
					a.id = @id
            ", joiner, new { id }));

			return await FillInUpstreamSources(a.SingleOrDefault());
		}

		async Task<T> FillInUpstreamSources<T>(T art) where T : Artist
		{
			if (art == null)
			{
				return null;
			}

			art.upstream_sources = await db.WithConnection(con => con.QueryAsync<ArtistUpstreamSource>(@"
				SELECT
					aus.*
				FROM
					artists_upstream_sources aus
				WHERE
					aus.artist_id = @artistId
			", new { artistId = art.id }));

			return art;
		}

		async Task<IEnumerable<T>> FillInUpstreamSources<T>(IEnumerable<T> art) where T : Artist
		{
			if (art == null)
			{
				return null;
			}

			var srcs = await db.WithConnection(con => con.QueryAsync(@"
				SELECT
					aus.*, s.*
				FROM
					artists_upstream_sources aus
					JOIN upstream_sources s ON s.id = aus.upstream_source_id
				WHERE
					aus.artist_id = ANY(@artistIds)
			", (ArtistUpstreamSource aus, UpstreamSource src) =>
			{
				aus.upstream_source = src;
				return aus;
			}, new { artistIds = art.Select(a => a.id).ToList() }));

			var gsrcs = srcs
				.Select(s =>
				{
					s.upstream_source.importer = _importService.ImporterForUpstreamSource(s.upstream_source);
					return s;
				})
				.GroupBy(src => src.artist_id)
				.ToDictionary(g => g.Key, g => g.Select(s => s))
				;

			foreach(var a in art)
			{
				IEnumerable<ArtistUpstreamSource> s;

				if(!gsrcs.TryGetValue(a.id, out s))
				{
					s = new List<ArtistUpstreamSource>();
				}

				a.upstream_sources = s;
			}

			return art;
		}

		public async Task<Artist> FindArtistWithIdOrSlug(string idOrSlug)
		{
			int id;
			Artist art = null;

			var baseSql = @"
                SELECT
                    a.*, f.*, au.*
                FROM
                    artists a

                    LEFT JOIN features f on f.artist_id = a.id
					LEFT JOIN artists_upstream_sources au ON au.artist_id = a.id
                WHERE
            ";

			if (int.TryParse(idOrSlug, out id))
			{
				art = await db.WithConnection(async con =>
				{
					var artists = await con.QueryAsync(
						baseSql + " a.id = @id",
						joiner,
						new { id = id }
					);

					return await FillInUpstreamSources(artists.FirstOrDefault());
				});
			}
			else
			{
				art = await db.WithConnection(async con =>
				{
					var artists = await con.QueryAsync(
						baseSql + " a.slug = @slug",
						joiner,
						new { slug = idOrSlug }
					);

					return await FillInUpstreamSources(artists.FirstOrDefault());
				});
			}

			return art;
		}
	}
}