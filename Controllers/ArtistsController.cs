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
    [Route("api/2/artists")]
    public class ArtistsController : RelistenBaseController
    {
        public ArtistsController(RedisService redis, DbService db) : base(redis, db) {}

        // GET api/values
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            return JsonSuccess(await db.QueryAsync<Artist>("select * from artists"));
        }

        // GET api/values/5
        [HttpGet("{idOrSlug}")]
        public async Task<IActionResult> Get(string idOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(idOrSlug);
            if(art != null) {
                return JsonSuccess(art);
            }

            return JsonNotFound();
        }

        private void update() {
            var updateDates = @"
            update shows s set updatedat = COALESCE((select MAX(updatedat) from tracks t WHERE t.showid = s.id), s.createdat);
            update years y set updatedat = COALESCE((select MAX(updatedat) from shows s WHERE s.year = y.year), y.createdat);
            update artists a set updatedat = COALESCE((select MAX(updatedat) from years y WHERE y.artistid = a.id), a.createdat);
            ";
        }
    }
}
