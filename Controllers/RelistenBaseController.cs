
using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Data;
using Relisten.Api.Models.Api;
using Relisten.Api.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Relisten.Api
{
    public class RelistenBaseController : Controller {
        protected RedisService redis {get;set;}
        protected IDbConnection db {get;set;}

        public RelistenBaseController(RedisService redis, DbService db) {
            this.redis = redis;
            this.db = db.connection;
        }

        protected IActionResult JsonSuccess(object anything) {
            return Json(ResponseEnvelope.Success(anything));
        }

        protected IActionResult JsonNotFound(object anything = null) {
            return NotFound(ResponseEnvelope.Error(ApiErrorCode.NotFound, anything));
        }

        protected async Task<Artist> FindArtistWithIdOrSlug(string idOrSlug) {
            int id;
            Artist art = null;

            if(int.TryParse(idOrSlug, out id)) {
                art = await db.QuerySingleAsync<Artist>("select * from artists where id = @id", new {id = id});
            }
            else {
                art = await db.QuerySingleAsync<Artist>("select * from artists where slug = @slug", new {slug = idOrSlug});
            }

            return art;
        }

    }
}