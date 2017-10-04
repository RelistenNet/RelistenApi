using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Services.Auth;

namespace Relisten.Controllers.Admin
{
    [ApiExplorerSettings(IgnoreApi = true)]
    public class AdminController : Controller
    {
		private readonly UserManager<ApplicationUser> _userManager;
		private readonly SignInManager<ApplicationUser> _signInManager;
		private readonly ILogger _logger;
		private readonly string _externalCookieScheme;
        readonly ArtistService _artistService;
        readonly UpstreamSourceService _upstreamSourceService;

        public AdminController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IOptions<IdentityCookieOptions> identityCookieOptions,
            ILoggerFactory loggerFactory,
            ArtistService artistService,
            UpstreamSourceService upstreamSourceService
        )
        {
            _upstreamSourceService = upstreamSourceService;
            _userManager = userManager;
			_signInManager = signInManager;
			_externalCookieScheme = identityCookieOptions.Value.ExternalCookieAuthenticationScheme;
			_logger = loggerFactory.CreateLogger<AdminController>();

            _artistService = artistService;
		}

		[HttpGet("/relisten-admin/login")]
		[AllowAnonymous]
		public async Task<IActionResult> Login(string returnUrl = null)
		{
			// Clear the existing external cookie to ensure a clean login process
			await HttpContext.Authentication.SignOutAsync(_externalCookieScheme);

			ViewData["ReturnUrl"] = returnUrl;
			return View();
		}

		[HttpPost("/relisten-admin/login")]
		[AllowAnonymous]
		//[ValidateAntiForgeryToken]
		public async Task<IActionResult> Login(LoginViewModel model, string returnUrl = null)
		{
			ViewData["ReturnUrl"] = returnUrl;
			if (ModelState.IsValid)
			{
				// This doesn't count login failures towards account lockout
				// To enable password failures to trigger account lockout, set lockoutOnFailure: true
                var result = await _signInManager.PasswordSignInAsync(model.Username, model.Password, model.RememberMe, lockoutOnFailure: true);
				if (result.Succeeded)
				{
					_logger.LogInformation(1, "User logged in.");
					return RedirectToLocal(returnUrl);
				}
				if (result.IsLockedOut)
				{
					_logger.LogWarning(2, "User account locked out.");
					return View("Lockout");
				}
				else
				{
					ModelState.AddModelError(string.Empty, "Invalid login attempt.");
					return View(model);
				}
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

		private IActionResult RedirectToLocal(string returnUrl)
		{
			if (Url.IsLocalUrl(returnUrl))
			{
				return Redirect(returnUrl);
			}
			else
			{
                return RedirectToAction(nameof(AdminController.Index), "Home");
			}
		}
    }

	public class LoginViewModel
	{
		[Required]
		public string Username { get; set; }

		[Required]
		[DataType(DataType.Password)]
		public string Password { get; set; }

		[Display(Name = "Remember me?")]
		public bool RememberMe { get; set; }
	}

    public class AdminViewModel
    {
        public IEnumerable<Artist> Artists { get; set; }
        public IEnumerable<UpstreamSource> UpstreamSources { get; set; }
    }
}
