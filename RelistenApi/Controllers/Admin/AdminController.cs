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

namespace Relisten.Controllers.Admin
{
    [ApiExplorerSettings(IgnoreApi = true)]
    public class AdminController : Controller
    {
        private readonly ArtistService _artistService;
        private readonly ILogger _logger;
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
            UpstreamSourceService upstreamSourceService
        )
        {
            _upstreamSourceService = upstreamSourceService;
            _userManager = userManager;
            _signInManager = signInManager;
            _logger = loggerFactory.CreateLogger<AdminController>();

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
}
