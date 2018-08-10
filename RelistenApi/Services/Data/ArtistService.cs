using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;
using Relisten.Import;
using System.Text;
using System.Reflection;

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
                )

                SELECT
                    a.*, show_count, source_count, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
                    LEFT JOIN show_counts sh ON sh.artist_id = a.id
                    LEFT JOIN (
						SELECT
							ssi.artist_id
							, SUM(ssi.source_count) as source_count
						FROM
							show_source_information ssi
						GROUP BY
							ssi.artist_id
					) src ON src.artist_id = a.id
				ORDER BY
					a.featured DESC, a.name
            ", (ArtistWithCounts artist, Features features) =>
			{
				artist.features = features;
				return artist;
			})));
		}

		public Task<int> RemoveAllContentForArtist(Artist art)
		{
			return db.WithConnection(con => con.ExecuteAsync(@"
				delete from setlist_songs where artist_id = @ArtistId;
				delete from setlist_shows where artist_id = @ArtistId;
				delete from shows where artist_id = @ArtistId;
				delete from sources where artist_id = @ArtistId;
				delete from tours where artist_id = @ArtistId;
				delete from venues where artist_id = @ArtistId;
				delete from years where artist_id = @ArtistId;
				delete from eras where artist_id = @ArtistId;
			", new {
				ArtistId = art.id
			}));
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
					a.featured DESC, a.sort_name
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

			var filled = await FillInUpstreamSources((IEnumerable<T>)new[] { art });
			return filled?.FirstOrDefault();
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
						baseSql + " a.slug = @slug OR a.uuid = @slug",
						joiner,
						new { slug = idOrSlug }
					);

					return await FillInUpstreamSources(artists.FirstOrDefault());
				});
			}

			return art;
		}

		string UpdateFieldsForFeatures(Features features)
		{
			var sb = new StringBuilder();
            var props = features.GetType().GetProperties().ToList().Where(p => p.Name != "id").ToArray();

			for (var i = 0; i < props.Length; i++)
			{
				sb.Append($"{props[i].Name} = @{props[i].Name}");

				if (i < props.Length - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}


		string InsertFieldsForFeatures(Features features)
		{
            var props = features.GetType().GetProperties().ToList().Where(p => p.Name != "id").ToArray();

			var sb = new StringBuilder();
            sb.AppendLine("(");

            for (var i = 0; i < props.Length; i++)
			{
				sb.Append($"{props[i].Name}");

                if(i < props.Length - 1) {
                    sb.Append(",");
                }

                sb.AppendLine();
			}

            sb.AppendLine(") VALUES (");

			for (var i = 0; i < props.Length; i++)
			{
				sb.Append($"@{props[i].Name}");

				if (i < props.Length - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(")");
			return sb.ToString();
		}

		public async Task<SlimArtistWithFeatures> Save(SlimArtistWithFeatures artist)
		{
			if (artist.id != 0)
			{
                var art = await db.WithConnection(async con => {
                    var innerArt = await con.QuerySingleAsync<SlimArtistWithFeatures>(@"
	                    UPDATE
	                        artists
	                    SET
	                        musicbrainz_id = @musicbrainz_id,
	                        name = @name,
	                        featured = @featured,
	                        slug = @slug,
	                        updated_at = timezone('utc'::text, now()),
							sort_name = @sort_name,
							uuid = md5('root::artist::' || @slug)::uuid
	                    WHERE
	                        id = @id
	                    RETURNING *
	                ", new
                    {
						artist.id,
                        artist.musicbrainz_id,
                        artist.name,
                        artist.slug,
                        artist.featured,
						sort_name = artist.name.Replace("The ", "")
					});

					innerArt.features = artist.features;
					innerArt.features.artist_id = innerArt.id;

					await con.ExecuteAsync(@"
                        UPDATE
                            features
                        SET
                            " + UpdateFieldsForFeatures(innerArt.features) + @"
                        WHERE
                            artist_id = @artist_id
                    ", innerArt.features);

                    return innerArt;
                });

                return art;
			}
			else
			{
                var art = await db.WithConnection(async con => {
                    var innerArt = await con.QuerySingleAsync<SlimArtistWithFeatures>(@"
	                    INSERT INTO
	                        artists

	                        (
	                            musicbrainz_id,
	                            featured,
	                            name,
	                            slug,
	                            updated_at,
								sort_name,
								uuid
	                        )
	                    VALUES
	                        (
	                            @musicbrainz_id,
	                            @featured,
	                            @name,
	                            @slug,
	                            timezone('utc'::text, now()),
								@sort_name,
								md5('root::artist::' || @slug)::uuid
	                        )
	                    RETURNING *
	                ", new
					{
						artist.musicbrainz_id,
						artist.name,
						artist.slug,
						artist.featured,
						sort_name = artist.name.Replace("The ", "")
					});

					innerArt.features = artist.features;
					innerArt.features.artist_id = innerArt.id;

					await con.ExecuteAsync(@"
                        INSERT INTO
                            features

                            " + InsertFieldsForFeatures(innerArt.features) + @"
                    ", innerArt.features);
					
                    return innerArt;
                });

				return art;
			}
		}
	}
}