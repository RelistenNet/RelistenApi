using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Api
{
    [ApiV3Formatting]
    [ApiCacheControl]
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
        protected bool IsV3Request => HttpContext?.Request?.Path.StartsWithSegments("/api/v3") ?? false;

        protected IActionResult JsonSuccess<T>(T anything)
        {
            return Json(anything);
        }

        protected IActionResult JsonNotFound<T>(T? anything = default)
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
            Artist? art,
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
            Func<IReadOnlyList<Artist>, Task<T>> cb,
            bool queryAllWhenEmpty = true
        )
        {
            if (!queryAllWhenEmpty && artistIdsOrSlugs.Count == 0)
            {
                var emptyData = await cb(new List<Artist>());
                if (emptyData == null)
                {
                    return JsonNotFound(false);
                }

                return JsonSuccess(emptyData);
            }

            var art = await _artistService.FindArtistsWithIdsOrSlugs(artistIdsOrSlugs);
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

    public class ApiCacheControlAttribute : ActionFilterAttribute
    {
        private const string DefaultCacheControl = "public, max-age=1800, immutable, stale-while-revalidate=60";
        private const string RandomCacheControl = "no-cache";

        public override void OnResultExecuting(ResultExecutingContext ctx)
        {
            var request = ctx.HttpContext.Request;
            if (!HttpMethods.IsGet(request.Method))
            {
                return;
            }

            if (!request.Path.StartsWithSegments("/api"))
            {
                return;
            }

            // Explicitly prevent caching of non-200 responses
            if (ctx.Result is IStatusCodeActionResult statusCodeResult)
            {
                var statusCode = statusCodeResult.StatusCode;
                if (statusCode.HasValue && statusCode.Value != 200)
                {
                    ctx.HttpContext.Response.Headers["Cache-Control"] = "no-store";
                    return;
                }
            }

            var path = request.Path.Value ?? string.Empty;
            var isRandom = path.IndexOf("/random", StringComparison.OrdinalIgnoreCase) >= 0;
            var isLive = path.IndexOf("/live", StringComparison.OrdinalIgnoreCase) >= 0;
            var cacheControl = (isRandom || isLive) ? RandomCacheControl : DefaultCacheControl;

            ctx.HttpContext.Response.Headers["Cache-Control"] = cacheControl;
        }
    }
}
