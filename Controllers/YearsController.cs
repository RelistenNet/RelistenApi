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
    public class YearsController : RelistenBaseController
    {
        public YearsController(RedisService redis, DbService db) : base(redis, db) {}

        // GET api/values
        [HttpGet("{idOrSlug}/years")]
        public async Task<IActionResult> Get(string idOrSlug)
        {
            Artist art = await FindArtistWithIdOrSlug(idOrSlug);
            if(art != null) {
                var yearsQuery = @"select * from years where artistid = @artistId";
                var years = await db.QueryAsync<Year>(yearsQuery, new {artistId = art.id});

                return JsonSuccess(years.Select(year => {
                    year.artist = art;
                    return year;
                }));
            }

            return JsonNotFound();
        }

        // GET api/values/5
        [HttpGet("{idOrSlug}/years/{year}")]
        public async Task<IActionResult> Get(string idOrSlug, int year)
        {
            Artist art = await FindArtistWithIdOrSlug(idOrSlug);
            if(art != null) {
                var recordings = await db.QueryAsync<SimpleRecording, Venue, SimpleRecording>(
                    @"
                    select
                        s.*,
                        s.average_rating as avg_rating,
                        v.*
                    from shows s
                        left join venues v on v.id = s.venueid
                    where
                        s.year = @year
                    and	s.artistid = @artistId
                    order by s.weighted_avg DESC
                    ",
                    (rec, venue) => {
                        rec.venue = venue;
                        return rec;
                    },
                    new {artistId = art.id, year = year}
                );

                if(recordings.Count() == 0) {
                    return JsonNotFound();
                }

                var shows = recordings
                    .GroupBy(rec => rec.display_date)
                    .Select(grp => {
                        var first = grp.First();

                        return new SimpleShow {
                            date = first.date,
                            display_date = first.display_date,
                            year = first.year,
                            artist = art,
                            avg_rating = grp.Where(rec => rec.avg_rating > 0).Average(rec => rec.avg_rating),
                            review_count = grp.Sum(rec => rec.reviews_count),
                            has_soundboard = grp.Count(rec => rec.is_soundboard) > 0,
                            avg_duration = (int)grp.Average(rec => rec.duration),
                            recordings = grp
                        };
                    })
                    ;

                var yearObj = await db.QueryFirstAsync<Year>("select * from years where artistid = @artistId and year = @year", new {
                    artistId = art.id,
                    year = year
                });

                yearObj.artist = art;
                yearObj.shows = shows;

                return JsonSuccess(yearObj);
            }

            return JsonNotFound();
        }
    }
}
