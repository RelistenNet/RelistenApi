namespace RelistenUserService.Authentication;

public sealed class SessionCookieManager
{
    public void SetAuthSso(HttpResponse response, IssuedIdentitySession session) =>
        Append(
            response,
            AuthenticationConstants.AuthSsoCookie,
            session.CookieValue,
            session.ExpiresAt);

    public void SetWeb(HttpResponse response, IssuedIdentitySession session) =>
        Append(
            response,
            AuthenticationConstants.WebSessionCookie,
            session.CookieValue,
            session.ExpiresAt);

    public void RenewWeb(
        HttpResponse response,
        string cookieValue,
        DateTimeOffset expiresAt) =>
        Append(
            response,
            AuthenticationConstants.WebSessionCookie,
            cookieValue,
            expiresAt);

    public void ClearAuthSso(HttpResponse response) =>
        Delete(response, AuthenticationConstants.AuthSsoCookie);

    public void ClearWeb(HttpResponse response) =>
        Delete(response, AuthenticationConstants.WebSessionCookie);

    public void ClearCsrf(HttpResponse response) =>
        Delete(response, AuthenticationConstants.CsrfCookie);

    private static void Append(
        HttpResponse response,
        string name,
        string value,
        DateTimeOffset expiresAt) =>
        response.Cookies.Append(name, value, Options(expiresAt));

    private static void Delete(HttpResponse response, string name) =>
        response.Cookies.Delete(name, Options(DateTimeOffset.UnixEpoch));

    private static CookieOptions Options(DateTimeOffset expiresAt) => new()
    {
        Expires = expiresAt,
        HttpOnly = true,
        IsEssential = true,
        Path = "/",
        SameSite = SameSiteMode.Lax,
        Secure = true
    };
}
