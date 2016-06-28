
using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Data;
using Relisten.Api.Models.Api;
using Relisten.Api.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace Relisten.Api
{
    public class RelistenBaseController : Controller
    {
        protected RedisService redis { get; set; }
        protected DbService db { get; set; }

        public RelistenBaseController(RedisService redis, DbService db)
        {
            this.redis = redis;
            this.db = db;
        }

        protected IActionResult JsonSuccess(object anything)
        {
            return Json(ResponseEnvelope.Success(anything));
        }

        protected IActionResult JsonNotFound(object anything = null)
        {
            return NotFound(ResponseEnvelope.Error(ApiErrorCode.NotFound, anything));
        }

        protected async Task<Artist> FindArtistWithIdOrSlug(string idOrSlug)
        {
            int id;
            Artist art = null;

            Func<Artist, Features, Artist> joiner = (Artist artist, Features features) =>
            {
                artist.features = features;
                return artist;
            };

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
                    var artists = await con.QueryAsync<Artist, Features, Artist>(
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
                    var artists = await con.QueryAsync<Artist, Features, Artist>(
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