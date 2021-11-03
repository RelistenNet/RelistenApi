
using Microsoft.AspNetCore.Mvc;
using Dapper;
using System.Data;
using Relisten.Api.Models.Api;
using Relisten.Api.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Serialization;
using Relisten.Data;

namespace Relisten.Api
{
    [ApiV3Formatting]
    public class RelistenBaseController : Controller
    {
        protected RedisService redis { get; set; }
		protected DbService db { get; set; }
		protected ArtistService _artistService { get; set; }

		public RelistenBaseController(RedisService redis, DbService db, ArtistService artistService)
        {
            this.redis = redis;
            this.db = db;
			_artistService = artistService;
        }

        protected IActionResult JsonSuccess<T>(T anything)
        {
            return Json(anything);
        }

        protected IActionResult JsonNotFound<T>(T anything = default(T))
        {
            return NotFound(ResponseEnvelope<T>.Error(ApiErrorCode.NotFound, anything));
        }

        protected async Task<IActionResult> ApiRequestWithIdentifier<T>(
            string artistIdOrSlug,
            string idAndSlug,
            Func<Artist, Identifier, Task<T>> cb,
            bool allowIdWithoutValue = false,
            bool isSlugOnly = false
        )
        {
            Artist art = await _artistService.FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var id = new Identifier(idAndSlug);

                if(isSlugOnly) {
                    id = new Identifier {
                        Slug = idAndSlug   
                    };
                }

                if (!id.Id.HasValue && !allowIdWithoutValue)
                {
                    return JsonNotFound(false);
                }

                var data = await cb(art, id);

                if (data == null)
                {
                    return JsonNotFound(false);
                }

                return JsonSuccess(data);
            }

            return JsonNotFound(false);
        }

        protected async Task<IActionResult> ApiRequest<T>(
            string artistIdOrSlug,
            Func<Artist, Task<T>> cb
        )
        {
            Artist art = await _artistService.FindArtistWithIdOrSlug(artistIdOrSlug);
            return await ApiRequest(art, cb);
        }
        
        protected async Task<IActionResult> ApiRequest<T>(
            Artist art,
            Func<Artist, Task<T>> cb
        )
        {
            if (art != null)
            {
                var data = await cb(art);

                if (data == null)
                {
                    return JsonNotFound(false);
                }

                return JsonSuccess(data);
            }

            return JsonNotFound(false);
        }

        protected async Task<IActionResult> ApiRequest<T>(
            IReadOnlyList<string> artistIdsOrSlugs,
            Func<IReadOnlyList<Artist>, Task<T>> cb
        )
        {
            IReadOnlyList<string> artistIdsSlugsOrUUIDs = artistIdsOrSlugs;
            if(artistIdsOrSlugs.Count == 1 && artistIdsOrSlugs[0][0] == '[')
            {
                artistIdsSlugsOrUUIDs = JsonConvert.DeserializeObject<List<string>>(artistIdsOrSlugs[0]);
            }

            IEnumerable<Artist> art = await _artistService.FindArtistsWithIdsOrSlugs(artistIdsSlugsOrUUIDs);
            if (art != null)
            {
                var data = await cb(art.ToList());

                if (data == null)
                {
                    return JsonNotFound(false);
                }

                return JsonSuccess(data);
            }

            return JsonNotFound(false);
        }
    }
    
    public class ApiV3FormattingAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuted(ActionExecutedContext ctx)
        {
            if (ctx.Result is not JsonResult objectResult)
            {
                return;
            }
            
            var isV3 = ctx.HttpContext.Request.Path.StartsWithSegments("/api/v3");

            objectResult.SerializerSettings = isV3
                ? RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings
                : RelistenApiJsonOptionsWrapper.DefaultSerializerSettings;
        }
    }
}