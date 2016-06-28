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
    public class SongsController : RelistenBaseController
    {
        private SetlistSongService _setlistSongService { get; set; }
        private SetlistShowService _setlistShowService { get; set; }

        public SongsController(
            RedisService redis,
            DbService db,
            SetlistSongService setlistSongService,
            SetlistShowService setlistShowService
        ) : base(redis, db)
        {
            _setlistSongService = setlistSongService;
            _setlistShowService = setlistShowService;
        }

        [HttpGet("{artistIdOrSlug}/songs")]
        public async Task<IActionResult> Songs(string artistIdOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var songs = await db.WithConnection(con => con.QueryAsync<SetlistSong>(@"
                    SELECT
                        s.*, COUNT(p.played_setlist_show_id) as shows_played_at
                    FROM
                        setlist_songs s
                        LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
                    WHERE
                        s.artist_id = @artistId
                    GROUP BY
                    	s.id
                    ORDER BY name
                ", new { artistId = art.id }));
                return JsonSuccess(songs);
            }

            return JsonNotFound();
        }
    }
}
