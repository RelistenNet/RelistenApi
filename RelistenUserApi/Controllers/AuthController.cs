using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Controllers;

[ApiController]
[Route("api/v3/library/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthenticatedUserContext _authenticatedUserContext;
    private readonly UserAuthService _authService;
    private readonly IHostEnvironment _environment;

    public AuthController(
        IAuthenticatedUserContext authenticatedUserContext,
        UserAuthService authService,
        IHostEnvironment environment)
    {
        _authenticatedUserContext = authenticatedUserContext;
        _authService = authService;
        _environment = environment;
    }

    [HttpPost("callback/{provider}")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Callback(
        [FromRoute] string provider,
        [FromBody] ProviderSignInRequest request)
    {
        try
        {
            return await _authService.SignInWithProvider(provider, request);
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    [HttpPost("development/session")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthTokenResponse>> DevelopmentSession(
        [FromBody] DevelopmentSessionRequest request)
    {
        if (!_environment.IsDevelopment() && !_environment.IsEnvironment("Test"))
        {
            return NotFound();
        }

        try
        {
            return await _authService.SignInDevelopmentUser(request);
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Refresh([FromBody] RefreshTokenRequest request)
    {
        try
        {
            return await _authService.Refresh(request.RefreshToken);
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    [Authorize]
    [HttpPost("reauthenticate/{provider}")]
    [ProducesResponseType(typeof(UserSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserSessionResponse>> Reauthenticate(
        [FromRoute] string provider,
        [FromBody] ProviderReauthenticationRequest request)
    {
        var user = _authenticatedUserContext.CurrentUser;
        try
        {
            return await _authService.Reauthenticate(
                user.UserUuid,
                user.SessionUuid,
                provider,
                request);
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(AuthErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        try
        {
            await _authService.Logout(request.RefreshToken);
            return NoContent();
        }
        catch (UserAuthException ex)
        {
            return AuthError(ex);
        }
    }

    private ObjectResult AuthError(UserAuthException ex)
    {
        var response = new AuthErrorResponse { Error = ex.Code };
        return ex.Code switch
        {
            "invalid_username" or "username_required" or "username_taken" or "provider_not_supported" =>
                BadRequest(response),
            "nonce_required" => BadRequest(response),
            _ => Unauthorized(response)
        };
    }
}
