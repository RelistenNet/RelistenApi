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
    public class ShowsController : RelistenBaseController
    {
        public ShowsController(RedisService redis, DbService db) : base(redis, db) { }

        [HttpGet()]
        [Route("{idOrSlug}/years/{year}/shows/{showIdOrDisplayDate}")]
        [Route("{idOrSlug}/show/{showIdOrDisplayDate}")]
        public async Task<IActionResult> Get(string idOrSlug, string showIdOrDisplayDate, int? year = null)
        {
            Artist art = await FindArtistWithIdOrSlug(idOrSlug);

            if (art == null)
            {
                return JsonNotFound("artist not found");
            }

            var recordings = await FindCompleteRecordingsWithIdOrDisplayDate(showIdOrDisplayDate, art);

            if (recordings.Count() == 0)
            {
                return JsonNotFound("show not found");
            }

            var tracks = (await db.QueryAsync<Track>(@"
                select
                    title,
                    md5,
                    track as trackNumber,
                    bitrate,
                    size as fileSize,
                    length as duration,
                    file as fileUrl,
                    slug,
                    id,
                    createdat,
                    updatedat,
                    showid
                from tracks
                where
                    t.showid in @showIds
            ", new
            {
                showIds = recordings.Select(rec => rec.id)
            }))
                .Select(track => {
                    track.artistId = art.id;
                    return track;
                })
                .GroupBy(track => track.showId)
                .ToDictionary(grp => grp.Key, grp => grp.ToList());

            foreach(var rec in recordings) {
                rec.tracks = tracks[rec.id].OrderBy(track => track.trackNumber);
            }

            var first = recordings.First();
            return JsonSuccess(new Show
            {
                date = first.date,
                display_date = first.display_date,
                year = first.year,
                artist = art,
                avg_rating = recordings
                    .Where(rec => rec.avg_rating > 0)
                    .Average(rec => rec.avg_rating),
                review_count = recordings.Sum(rec => rec.reviews_count),
                has_soundboard = recordings.Count(rec => rec.is_soundboard) > 0,
                avg_duration = (int)recordings.Average(rec => rec.duration),
                recordings = recordings
            });
        }
    }
}
