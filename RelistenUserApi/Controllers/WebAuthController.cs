using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Route("api/v3/library/auth/web")]
public sealed class WebAuthController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly UserAuthService _authService;
    private readonly WebOAuthService _webOAuthService;
    private readonly WebSessionCookieService _cookieService;

    public WebAuthController(
        IAuthenticatedUserContext authenticatedUserContext,
        UserAuthService authService,
        WebOAuthService webOAuthService,
        WebSessionCookieService cookieService)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _authService = authService;
        _webOAuthService = webOAuthService;
        _cookieService = cookieService;
    }

    [HttpGet("google/start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult StartGoogle([FromQuery(Name = "return_url")] string? returnUrl)
    {
        try
        {
            var start = _webOAuthService.StartGoogle(Request, returnUrl);
            _cookieService.SetOAuthStateCookie(Response, start.ProtectedState, start.StateExpiresAt);
            return Redirect(start.AuthorizationUrl);
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    [HttpGet("google/callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GoogleCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        var protectedState = _cookieService.ReadOAuthStateCookie(Request);
        _cookieService.DeleteOAuthStateCookie(Response);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return BadRequest(new AuthErrorResponse { Error = "provider_denied" });
        }

        try
        {
            var callback = await _webOAuthService.CompleteGoogleCallback(
                Request,
                code,
                state,
                protectedState);
            await _cookieService.SignIn(HttpContext, callback.Session);
            return Redirect(callback.ReturnUrl);
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    [Authorize]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Logout()
    {
        var user = _authenticatedUserContext.CurrentUser;
        if (user.SessionUuid.HasValue)
        {
            await _authService.RevokeSession(user.UserUuid, user.SessionUuid.Value);
        }

        await _cookieService.SignOut(HttpContext);
        return NoContent();
    }

    private ObjectResult AuthError(UserAuthException ex)
    {
        var response = new AuthErrorResponse { Error = ex.Code };
        return ex.Code switch
        {
            "authorization_code_required" or "invalid_oauth_state" or "invalid_return_url" =>
                BadRequest(response),
            _ => Unauthorized(response)
        };
    }
}
