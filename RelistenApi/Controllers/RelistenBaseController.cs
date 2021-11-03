using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Api
{
    [ApiV3Formatting]
    public class RelistenBaseController : Controller
    {
        public RelistenBaseController(RedisService redis, DbService db, ArtistService artistService)
        {
            this.redis = redis;
            this.db = db;
            _artistService = artistService;
        }

        protected RedisService redis { get; set; }
        protected DbService db { get; set; }
        protected ArtistService _artistService { get; set; }

        protected IActionResult JsonSuccess<T>(T anything)
        {
            return Json(anything);
        }

        protected IActionResult JsonNotFound<T>(T anything = default)
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
            var art = await _artistService.FindArtistWithIdOrSlug(artistIdOrSlug);
            if (art != null)
            {
                var id = new Identifier(idAndSlug);

                if (isSlugOnly)
                {
                    id = new Identifier {Slug = idAndSlug};
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
            var art = await _artistService.FindArtistWithIdOrSlug(artistIdOrSlug);
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
            var artistIdsSlugsOrUUIDs = artistIdsOrSlugs;
            if (artistIdsOrSlugs.Count == 1 && artistIdsOrSlugs[0][0] == '[')
            {
                artistIdsSlugsOrUUIDs = JsonConvert.DeserializeObject<List<string>>(artistIdsOrSlugs[0]);
            }

            var art = await _artistService.FindArtistsWithIdsOrSlugs(artistIdsSlugsOrUUIDs);
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
