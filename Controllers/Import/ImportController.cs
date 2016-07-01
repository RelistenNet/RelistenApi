using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Import;

namespace Relisten.Controllers
{
    [Route("api/2/import/")]
    public class ImportController : RelistenBaseController
    {
        protected Importer _importer { get; set; }

        public ImportController(
            RedisService redis,
            DbService db,
            Importer importer
        ) : base(redis, db)
        {
            _importer = importer;
        }

        [HttpGet("{idOrSlug}")]
        public async Task<IActionResult> Get(string idOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(idOrSlug);
            if (art != null)
            {
                return JsonSuccess(await _importer.Import(art));
            }

            return JsonNotFound();
        }

        // private void update()
        // {
        //     var updateDates = @"
        //     update shows s set updatedat = COALESCE((select MAX(updatedat) from tracks t WHERE t.showid = s.id), s.createdat);
        //     update years y set updatedat = COALESCE((select MAX(updatedat) from shows s WHERE s.year = y.year), y.createdat);
        //     update artists a set updatedat = COALESCE((select MAX(updatedat) from years y WHERE y.artistid = a.id), a.createdat);
        //     ";
        // }
    }
}
