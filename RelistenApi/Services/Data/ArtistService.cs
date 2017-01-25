using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Relisten.Data
{
	public class ArtistService : RelistenDataServiceBase
	{
		public ArtistService(DbService db) : base(db)
		{
		}

		private static Func<Artist, Features, Artist> joiner = (Artist artist, Features features) =>
			{
				artist.features = features;
				return artist;
			};

		public async Task<IEnumerable<ArtistWithCounts>> AllWithCounts()
		{
			return await db.WithConnection(con => con.QueryAsync(@"
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
			}));
		}

		public async Task<IEnumerable<Artist>> All()
		{
			return await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
				ORDER BY
					a.featured, a.name
            ", joiner));
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

			return a.SingleOrDefault();
		}

		public async Task<Artist> FindArtistWithIdOrSlug(string idOrSlug)
		{
			int id;
			Artist art = null;

			var baseSql = @"
                SELECT
                    a.*, f.*
                FROM
                    artists a

                    LEFT JOIN features f on f.artist_id = a.id 
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

					return artists.FirstOrDefault();
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

					return artists.FirstOrDefault();
				});
			}

			return art;
		}
	}
}