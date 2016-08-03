using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Api.Models.Api;

namespace Relisten.Controllers
{
    [Route("api/v2")]
    [Produces("application/json")]
    public class ArtistsController : RelistenBaseController
    {
        public ShowService _showService { get; set; }

        public ArtistsController(
            RedisService redis,
            DbService db,
            ShowService showService
        ) : base(redis, db)
        {
            _showService = showService;
        }

        // GET api/values
        [HttpGet("artists")]
        [ProducesResponseType(typeof(ResponseEnvelope<IEnumerable<Artist>>), 200)]
        public async Task<IActionResult> Get()
        {
            return JsonSuccess(await db.WithConnection(conn => conn.QueryAsync<ArtistWithCounts, Features, ArtistWithCounts>(@"
                WITH show_counts AS (
                    SELECT
                        artist_id,
                        COUNT(*) as show_count
                    FROM
                        shows
                    GROUP BY
                        artist_id
                ), source_counts AS (
                    SELECT
                        artist_id,
                        COUNT(*) as source_count
                    FROM
                        sources
                    GROUP BY
                        artist_id	
                )

                SELECT
                    a.*, f.*, show_count, source_count
                FROM
                    artists a
                    LEFT JOIN features f on f.artist_id = a.id
                    LEFT JOIN show_counts sh ON sh.artist_id = a.id
                    LEFT JOIN source_counts src ON src.artist_id = a.id
                ", (artist, features) =>
            {
                artist.features = features;
                return artist;
            }))
            );
        }

        // GET api/values/5
        [HttpGet("artists/{artistIdOrSlug}")]
        [ProducesResponseType(typeof(ResponseEnvelope<Artist>), 200)]
        [ProducesResponseType(typeof(ResponseEnvelope<bool>), 404)]
        public async Task<IActionResult> Get(string artistIdOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                return JsonSuccess(art);
            }

            return JsonNotFound(false);
        }
    }
}
