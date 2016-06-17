using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Controllers
{
    [Route("api/2/[controller]")]
    public class ArtistsController : RelistenBaseController
    {
        public ArtistsController(RedisService redis, DbService db) : base(redis, db) {}

        // GET api/values
        [HttpGet]
        public IActionResult Get()
        {
            return JsonSuccess(db.Query<Artist>("select * from artists"));
        }

        // GET api/values/5
        [HttpGet("{idOrSlug}")]
        public IActionResult Get(string idOrSlug)
        {
            int id;
            IEnumerable<Artist> art;

            if(int.TryParse(idOrSlug, out id)) {
                art = db.Query<Artist>("select * from artists where id = @id", new {id = id});
            }
            else {
                art = db.Query<Artist>("select * from artists where slug = @slug", new {slug = idOrSlug});
            }

            if(art.Count() > 0) {
                return JsonSuccess(art.FirstOrDefault());
            }

            return JsonNotFound();
        }
    }
}
