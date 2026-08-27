using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Authentication;

public sealed class AuthSsoSignInService(
    IdentitySessionLifecycle sessions,
    SessionCookieManager cookies)
{
    public async Task SignInAsync(
        HttpResponse response,
        User user,
        CancellationToken cancellationToken)
    {
        var session = await sessions.CreateAuthSsoAsync(user.Id, cancellationToken);
        cookies.SetAuthSso(response, session);
    }
}
