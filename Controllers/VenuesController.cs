using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor;
using System.Linq.Expressions;

namespace Relisten.Controllers
{
    [Route("api/2/artists")]
    public class VenuesController : RelistenBaseController
    {
        private VenueService _venueService { get; set; }

        public VenuesController(
            RedisService redis,
            DbService db,
            VenueService venueService
        ) : base(redis, db)
        {
            _venueService = venueService;
        }

        [HttpGet("{artistIdOrSlug}/venues")]
        public async Task<IActionResult> tours(string artistIdOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                return JsonSuccess(await _venueService.AllForArtist(art));
            }

            return JsonNotFound();
        }
    }
}
