
using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Data;
using Relisten.Api.Models.Api;

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
    }
}