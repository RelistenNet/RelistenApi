using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Auth;

namespace RelistenUserApiTests;

public sealed class TestUserAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string UserUuidHeader = "X-Relisten-Test-User-Uuid";
    public const string DisplayNameHeader = "X-Relisten-Test-Display-Name";
    public const string UsernameHeader = "X-Relisten-Test-Username";
    public const string ScopeIdHeader = "X-Relisten-Test-Scope-Id";

    public TestUserAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserUuidHeader, out var userUuidValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Guid.TryParse(userUuidValues.ToString(), out var userUuid))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid test user UUID."));
        }

        var displayName = HeaderOrDefault(DisplayNameHeader, "Test User");
        var username = HeaderOrDefault(UsernameHeader, "test-user");
        var scopeId = HeaderOrDefault(ScopeIdHeader, $"user:{userUuid}");

        var claims = new[]
        {
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid, userUuid.ToString()),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.DisplayName, displayName),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.Username, username),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.ScopeId, scopeId),
            new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.SessionUuid, Guid.NewGuid().ToString())
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string HeaderOrDefault(string header, string defaultValue)
    {
        return Request.Headers.TryGetValue(header, out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : defaultValue;
    }
}
