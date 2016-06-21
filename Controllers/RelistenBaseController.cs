
using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Data;
using Relisten.Api.Models.Api;
using Relisten.Api.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;

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

        protected async Task<Artist> FindArtistWithIdOrSlug(string idOrSlug) {
            int id;
            Artist art = null;

            if(int.TryParse(idOrSlug, out id)) {
                art = await db.QuerySingleAsync<Artist>("select * from artists where id = @id", new {id = id});
            }
            else {
                art = await db.QuerySingleAsync<Artist>("select * from artists where slug = @slug", new {slug = idOrSlug});
            }

            return art;
        }
         protected async Task<IEnumerable<Recording>> FindCompleteRecordingsWithIdOrDisplayDate(string idOrDisplayDate, Artist forArtist) {
            int id;
            string query = "";
            object p = new {};

            if(int.TryParse(idOrDisplayDate, out id)) {
                query = @"
                select s.*, v.*
                from shows s
                    left join venues v on v.id = s.venueid 
                where
                    id = @id
                ";
                p = new {id = id};
            }
            else {
                query = @"
                select s.*, v.*
                from shows s
                    left join venues v on v.id = s.venueid 
                where
                    display_date = @displayDate
                and artistid = @artistId
                ";
                p = new {
                    displayDate = idOrDisplayDate,
                    artistId = forArtist.id
                };
            }

            return await db.QueryAsync<Recording, Venue, Recording>(query, (recording, ven) => {
                recording.venue = ven;
                recording.recordingReviews = JsonConvert.DeserializeObject<IList<RecordingReview>>(recording.reviews);
                return recording;
            }, p);
        }
    }
}