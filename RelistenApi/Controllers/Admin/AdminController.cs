using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Services.Auth;
using Relisten.Services.Popularity;

namespace Relisten.Controllers.Admin
{
    [ApiExplorerSettings(IgnoreApi = true)]
    public class AdminController : Controller
    {
        private readonly ArtistService _artistService;
        private readonly ILogger _logger;
        private readonly PopularityCacheService _popularityCacheService;
        private readonly PopularityJobs _popularityJobs;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ScheduledService _scheduledService;
        private readonly UpstreamSourceService _upstreamSourceService;
        private readonly UserManager<ApplicationUser> _userManager;

        public AdminController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ILoggerFactory loggerFactory,
            ArtistService artistService,
            ScheduledService scheduledService,
            UpstreamSourceService upstreamSourceService,
            PopularityJobs popularityJobs,
            PopularityCacheService popularityCacheService
        )
        {
            _upstreamSourceService = upstreamSourceService;
            _userManager = userManager;
            _signInManager = signInManager;
            _logger = loggerFactory.CreateLogger<AdminController>();
            _popularityJobs = popularityJobs;
            _popularityCacheService = popularityCacheService;

            _artistService = artistService;
            _scheduledService = scheduledService;
        }

        [HttpGet("/relisten-admin/login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login(string? returnUrl = null)
        {
            // Clear the existing external cookie to ensure a clean login process
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost("/relisten-admin/login")]
        [AllowAnonymous]
        //[ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            if (ModelState.IsValid)
            {
                // This doesn't count login failures towards account lockout
                // To enable password failures to trigger account lockout, set lockoutOnFailure: true
                var result =
                    await _signInManager.PasswordSignInAsync(model.Username, model.Password, model.RememberMe,
                        true);
                if (result.Succeeded)
                {
                    _logger.LogInformation(1, "User logged in.");
                    return RedirectToLocal(returnUrl);
                }

                if (result.IsLockedOut)
                {
                    _logger.LogWarning(2, "User account locked out.");
                    return Content("Locked out");
                }

                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return View(model);
            }

            // If we got this far, something failed, redisplay form
            return View(model);
        }

        [HttpGet("/relisten-admin/")]
        [Authorize]
        public async Task<IActionResult> Index()
        {
            return View(new AdminViewModel
            {
                Artists = await _artistService.All(),
                UpstreamSources = await _upstreamSourceService.AllUpstreamSources()
            });
        }

        [HttpGet("/relisten-admin/popularity")]
        [Authorize]
        public IActionResult Popularity()
        {
            ViewBag.Message = TempData["message"];
            return View(new PopularityAdminViewModel
            {
                CacheEntries = new[]
                {
                    new PopularityCacheEntryViewModel("popularity:artists:map:30d-48h", GetCacheStatus("popularity:artists:map:30d-48h")),
                    new PopularityCacheEntryViewModel("popularity:artists:hot:30d:50", GetCacheStatus("popularity:artists:hot:30d:50")),
                    new PopularityCacheEntryViewModel("popularity:artists:trending:48h:50", GetCacheStatus("popularity:artists:trending:48h:50")),
                    new PopularityCacheEntryViewModel("popularity:shows:hot:30d:50", GetCacheStatus("popularity:shows:hot:30d:50")),
                    new PopularityCacheEntryViewModel("popularity:shows:trending:48h:50", GetCacheStatus("popularity:shows:trending:48h:50")),
                    new PopularityCacheEntryViewModel("popularity:years:hot:30d:50", GetCacheStatus("popularity:years:hot:30d:50")),
                    new PopularityCacheEntryViewModel("popularity:years:trending:48h:50", GetCacheStatus("popularity:years:trending:48h:50"))
                }
            });
        }

        [HttpPost("/relisten-admin/popularity/refresh-artists-map")]
        [Authorize]
        public IActionResult RefreshArtistPopularityMap()
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshArtistPopularityMap());
            TempData["message"] = $"Queued artist popularity refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-shows-map")]
        [Authorize]
        public IActionResult RefreshShowPopularityMap([FromQuery] string artistUuid)
        {
            if (!Guid.TryParse(artistUuid, out var uuid))
            {
                TempData["message"] = "Invalid artist UUID.";
                return RedirectToAction(nameof(Popularity));
            }

            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshShowPopularityMapForArtist(uuid));
            TempData["message"] = $"Queued show popularity refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-years-map")]
        [Authorize]
        public IActionResult RefreshYearPopularityMap([FromQuery] string artistUuid)
        {
            if (!Guid.TryParse(artistUuid, out var uuid))
            {
                TempData["message"] = "Invalid artist UUID.";
                return RedirectToAction(nameof(Popularity));
            }

            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshYearPopularityMapForArtist(uuid));
            TempData["message"] = $"Queued year popularity refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-popular-artists")]
        [Authorize]
        public IActionResult RefreshPopularArtists([FromQuery] int limit = 50)
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularArtists(limit));
            TempData["message"] = $"Queued popular artists refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-trending-artists")]
        [Authorize]
        public IActionResult RefreshTrendingArtists([FromQuery] int limit = 50)
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingArtists(limit));
            TempData["message"] = $"Queued trending artists refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-popular-shows")]
        [Authorize]
        public IActionResult RefreshPopularShows([FromQuery] int limit = 50)
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularShows(limit));
            TempData["message"] = $"Queued popular shows refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-trending-shows")]
        [Authorize]
        public IActionResult RefreshTrendingShows([FromQuery] int limit = 50)
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingShows(limit));
            TempData["message"] = $"Queued trending shows refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-popular-years")]
        [Authorize]
        public IActionResult RefreshPopularYears([FromQuery] int limit = 50)
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularYears(limit));
            TempData["message"] = $"Queued popular years refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-trending-years")]
        [Authorize]
        public IActionResult RefreshTrendingYears([FromQuery] int limit = 50)
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingYears(limit));
            TempData["message"] = $"Queued trending years refresh as job {jobId}.";
            return RedirectToAction(nameof(Popularity));
        }

        [HttpPost("/relisten-admin/popularity/refresh-all")]
        [Authorize]
        public IActionResult RefreshAllPopularity([FromQuery] int limit = 50)
        {
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshArtistPopularityMap());
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularArtists(limit));
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingArtists(limit));
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularShows(limit));
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingShows(limit));
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshPopularYears(limit));
            Hangfire.BackgroundJob.Enqueue(() => _popularityJobs.RefreshTrendingYears(limit));

            TempData["message"] = "Queued refresh for all global popularity caches.";
            return RedirectToAction(nameof(Popularity));
        }

        private PopularityCacheHeaderResult GetCacheStatus(string key)
        {
            return _popularityCacheService.GetHeaderAsync(key).GetAwaiter().GetResult();
        }

        [HttpPost("/relisten-admin/index-archiveorg")]
        [Authorize]
        public IActionResult IndexArchiveOrgArtists()
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _scheduledService.IndexArchiveOrgArtists(null, allowedInDev: true));
            return Json(new {message = $"Queued as job {jobId}!"});
        }

        [HttpPost("/relisten-admin/refresh-all-artists")]
        [Authorize]
        public IActionResult RefreshAllArtists()
        {
            var jobId = Hangfire.BackgroundJob.Enqueue(() => _scheduledService.RefreshAllArtists(null, allowedInDev: true));
            return Json(new {message = $"Queued as job {jobId}!"});
        }

        [HttpPost("/relisten-admin/backfill-jerrygarcia-venues")]
        [Authorize]
        public IActionResult BackfillJerryGarciaVenues([FromQuery] string artistSlug = "grateful-dead")
        {
            var jobId =
                Hangfire.BackgroundJob.Enqueue(() => _scheduledService.BackfillJerryGarciaVenues(artistSlug, null));
            return Json(new {message = $"Queued as job {jobId}!"});
        }

        private IActionResult RedirectToLocal(string? returnUrl)
        {
            if (Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index), "Admin");
        }
    }

    public class LoginViewModel
    {
        [Required] public string Username { get; set; } = null!;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = null!;

        [Display(Name = "Remember me?")] public bool RememberMe { get; set; }
    }

    public class AdminViewModel
    {
        public IEnumerable<Artist> Artists { get; set; } = null!;
        public IEnumerable<UpstreamSource> UpstreamSources { get; set; } = null!;
    }

    public class PopularityAdminViewModel
    {
        public IReadOnlyList<PopularityCacheEntryViewModel> CacheEntries { get; set; } =
            Array.Empty<PopularityCacheEntryViewModel>();
    }

    public class PopularityCacheEntryViewModel
    {
        public PopularityCacheEntryViewModel(string key, PopularityCacheHeaderResult status)
        {
            Key = key;
            Status = status;
        }

        public string Key { get; }
        public PopularityCacheHeaderResult Status { get; }
    }
}
