using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Services;

namespace Relisten.UserApi.Auth;

public sealed class AccessTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AccessTokenService _accessTokenService;
    private readonly IUserAuthStore _authStore;

    public AccessTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccessTokenService accessTokenService,
        IUserAuthStore authStore)
        : base(options, logger, encoder)
    {
        _accessTokenService = accessTokenService;
        _authStore = authStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        var principal = _accessTokenService.Validate(token);
        if (principal == null)
        {
            return AuthenticateResult.Fail("Invalid access token.");
        }

        var userUuidValue = principal.FindFirstValue(RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid);
        if (!Guid.TryParse(userUuidValue, out var userUuid))
        {
            return AuthenticateResult.Fail("Access token is missing a valid user.");
        }

        var sessionUuidValue = principal.FindFirstValue(RelistenUserAuthenticationDefaults.ClaimTypes.SessionUuid);
        if (!Guid.TryParse(sessionUuidValue, out var sessionUuid))
        {
            return AuthenticateResult.Fail("Access token is missing a valid session.");
        }

        var session = await _authStore.GetSession(sessionUuid);
        if (session == null || session.RevokedAt != null || session.UserUuid != userUuid)
        {
            return AuthenticateResult.Fail("Session is not active.");
        }

        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
