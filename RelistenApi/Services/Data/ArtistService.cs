using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Import;

namespace Relisten.Data
{
    public class ArtistService : RelistenDataServiceBase
    {
        private static Func<Artist, Features, Artist> joiner = (Artist artist, Features features) =>
        {
            artist.features = features;
            return artist;
        };

        private readonly ImporterService _importService;

        public ArtistService(DbService db, ImporterService importService) : base(db)
        {
            _importService = importService;
        }

        public async Task<IEnumerable<T>> AllWithCounts<T>(IReadOnlyList<string>? idsOrSlugs = null,
            bool includeAutoCreated = true)
            where T : ArtistWithCounts
        {
            var where = "";
            if (idsOrSlugs == null || idsOrSlugs.Count == 0)
            {
                if (includeAutoCreated)
                {
                    where = "1=1 AND (COALESCE(sh.show_count, 0) > 0 OR COALESCE(src.source_count, 0) > 0)";
                }
                else
                {
                    where = "(a.featured & @autoCreatedFlag) = 0";
                }
            }
            else
            {
                where = " a.id = ANY(@ids) OR a.slug = ANY(@slugs) OR a.uuid = ANY(@uuids)";
            }

            var ids = new List<int>();
            var slugs = new List<string>();
            var uuids = new List<Guid>();

            if (idsOrSlugs != null)
            {
                foreach (var idOrSlug in idsOrSlugs)
                {
                    FlexiblyParseArtistIdentifier(idOrSlug, ids, uuids, slugs);
                }
            }

            return await FillInUpstreamSources(await db.WithConnection(con => con.QueryAsync($@"
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
				WHERE
					{where}
				ORDER BY
					a.featured DESC, a.name
            ", (T artist, Features features) =>
            {
                artist.features = features;
                return artist;
            }, new
            {
                ids,
                uuids,
                slugs,
                autoCreatedFlag = (int)ArtistFeaturedFlags.AutoCreated
            })));
        }

        public Task<int> RemoveAllContentForArtist(Artist art)
        {
            // Order matters: delete child tables before parent tables due to FK constraints
            return db.WithWriteConnection(con => con.ExecuteAsync(@"
				delete from setlist_songs where artist_id = @ArtistId;
				delete from setlist_shows where artist_id = @ArtistId;
				delete from source_tracks where artist_id = @ArtistId;
				delete from source_sets where source_id in (select id from sources where artist_id = @ArtistId);
				delete from source_reviews where source_id in (select id from sources where artist_id = @ArtistId);
				delete from shows where artist_id = @ArtistId;
				delete from sources where artist_id = @ArtistId;
				delete from tours where artist_id = @ArtistId;
				delete from venues where artist_id = @ArtistId;
				delete from years where artist_id = @ArtistId;
				delete from eras where artist_id = @ArtistId;
			", new {ArtistId = art.id}));
        }

        public async Task<IEnumerable<Artist>> All(bool includeAutoCreated = true)
        {
            var where = includeAutoCreated ? "1=1" : "(a.featured & @autoCreatedFlag) = 0";

            return await FillInUpstreamSources(await db.WithConnection(con => con.QueryAsync($@"
                SELECT
                    a.*, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
				WHERE
                    {where}
				ORDER BY
					a.featured DESC, a.sort_name
            ", joiner, new {autoCreatedFlag = (int)ArtistFeaturedFlags.AutoCreated})));
        }

        public async Task<Artist?> FindArtistById(int id)
        {
            var a = await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
				WHERE
					a.id = @id
            ", joiner, new {id}));

            return await FillInUpstreamSources(a.SingleOrDefault());
        }

        public async Task<Artist?> FindArtistByUuid(Guid uuid)
        {
            var a = await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
				WHERE
					a.uuid = @uuid
            ", joiner, new {uuid}));

            return await FillInUpstreamSources(a.SingleOrDefault());
        }

        public async Task<Artist?> FindArtistByShowUuid(Guid uuid)
        {
            var a = await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    shows s
                    JOIN artists a ON s.artist_id = a.id
                    LEFT JOIN features f on f.artist_id = a.id
				WHERE
					s.uuid = @uuid
                LIMIT 1
            ", joiner, new {uuid}));

            return await FillInUpstreamSources(a.SingleOrDefault());
        }

        public async Task<Artist?> FindArtistBySourceUuid(Guid uuid)
        {
            var a = await db.WithConnection(con => con.QueryAsync(@"
                SELECT
                    a.*, f.*
                FROM
                    sources s
                    JOIN artists a ON s.artist_id = a.id
                    LEFT JOIN features f on f.artist_id = a.id
				WHERE
					s.uuid = @uuid
                LIMIT 1
            ", joiner, new {uuid}));

            return await FillInUpstreamSources(a.SingleOrDefault());
        }

        async Task<T?> FillInUpstreamSources<T>(T? art) where T : Artist
        {
            if (art == null)
            {
                return null;
            }

            var filled = await FillInUpstreamSources((IEnumerable<T>)new[] {art});
            return filled?.FirstOrDefault();
        }

        async Task<IEnumerable<T>> FillInUpstreamSources<T>(IEnumerable<T>? art) where T : Artist
        {
            if (art == null)
            {
                return Array.Empty<T>();
            }

            var srcs = await db.WithConnection(con => con.QueryAsync(@"
				SELECT
					aus.*, a.uuid as artist_uuid, s.*
				FROM
					artists_upstream_sources aus
					JOIN upstream_sources s ON s.id = aus.upstream_source_id
				    JOIN artists a on a.id = aus.artist_id
				WHERE
					aus.artist_id = ANY(@artistIds)
			", (ArtistUpstreamSource aus, UpstreamSource src) =>
            {
                aus.upstream_source = src;
                return aus;
            }, new {artistIds = art.Select(a => a.id).ToList()}));

            var gsrcs = srcs
                    .Select(s =>
                    {
                        s.upstream_source.importer = _importService.ImporterForUpstreamSource(s.upstream_source);
                        return s;
                    })
                    .GroupBy(src => src.artist_id)
                    .ToDictionary(g => g.Key, g => g.Select(s => s))
                ;

            foreach (var a in art)
            {
                if (!gsrcs.TryGetValue(a.id, out var sources))
                {
                    sources = Array.Empty<ArtistUpstreamSource>();
                }

                a.upstream_sources = sources;
            }

            return art;
        }

        public async Task<Artist?> FindArtistWithIdOrSlug(string idOrSlug)
        {
            return (await FindArtistsWithIdsOrSlugs(new[] {idOrSlug})).FirstOrDefault();
        }

        public async Task<IEnumerable<Artist>> FindArtistsWithIdsOrSlugs(IReadOnlyList<string> idsOrSlugs)
        {
            var baseSql = @"
                SELECT
                    a.*, f.*, au.*
                FROM
                    artists a

                    LEFT JOIN features f on f.artist_id = a.id
					LEFT JOIN artists_upstream_sources au ON au.artist_id = a.id
                WHERE
            ";

            var ids = new List<int>();
            var slugs = new List<string>();
            var uuids = new List<Guid>();

            foreach (var idOrSlug in idsOrSlugs)
            {
                FlexiblyParseArtistIdentifier(idOrSlug, ids, uuids, slugs);
            }

            return await db.WithConnection(async con =>
            {
                string where;
                if (idsOrSlugs.Count == 0)
                {
                    where = "1=1";
                }
                else
                {
                    where = " a.id = ANY(@ids) OR a.slug = ANY(@slugs) OR a.uuid = ANY(@uuids)";
                }

                var artists = await con.QueryAsync(
                    baseSql + where,
                    joiner,
                    new {ids, slugs, uuids}
                );

                return await FillInUpstreamSources(artists);
            });
        }

        private static void FlexiblyParseArtistIdentifier(string idOrSlug, List<int> ids, List<Guid> guids,
            List<string> slugs)
        {
            if (int.TryParse(idOrSlug, out var id))
            {
                ids.Add(id);
                return;
            }

            if (idOrSlug.Length == 36 && Guid.TryParse(idOrSlug, out var guid))
            {
                guids.Add(guid);
                return;
            }

            slugs.Add(idOrSlug);
        }

        public async Task<Artist?> FindArtistByUpstreamIdentifier(int upstreamSourceId, string upstreamIdentifier)
        {
            return await db.WithConnection(async con =>
            {
                var artist = await con.QueryAsync(@"
                    SELECT
                        a.*, f.*
                    FROM
                        artists a
                        JOIN artists_upstream_sources aus ON aus.artist_id = a.id
                        LEFT JOIN features f on f.artist_id = a.id
                    WHERE
                        aus.upstream_source_id = @upstreamSourceId
                        AND aus.upstream_identifier = @upstreamIdentifier
                    LIMIT 1
                ", joiner, new {upstreamSourceId, upstreamIdentifier});

                return (await FillInUpstreamSources(artist)).FirstOrDefault();
            });
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

                if (i < props.Length - 1)
                {
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
                var art = await db.WithWriteConnection(async con =>
                {
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
                var art = await db.WithWriteConnection(async con =>
                {
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
