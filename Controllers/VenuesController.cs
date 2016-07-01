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
        private ShowService _showService { get; set; }

        public VenuesController(
            RedisService redis,
            DbService db,
            VenueService venueService,
            ShowService showService
        ) : base(redis, db)
        {
            _venueService = venueService;
            _showService = showService;
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

        [HttpGet("{artistIdOrSlug}/venues/{idAndSlug}")]
        public async Task<IActionResult> years(string artistIdOrSlug, string idAndSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var id = new Identifier(idAndSlug);
                if (!id.Id.HasValue)
                {
                    return JsonNotFound();
                }

                var venue = await _venueService.ForId(id.Id.Value);

                if (venue == null)
                {
                    return JsonNotFound();
                }

                venue.shows = new List<Show>();
                venue.shows.AddRange(await _showService.ShowsForCriteria(
                    "s.venue_id = @venue_id",
                    new { venue_id = venue.id }
                ));

                return JsonSuccess(venue);
            }

            return JsonNotFound();
        }
    }
}
