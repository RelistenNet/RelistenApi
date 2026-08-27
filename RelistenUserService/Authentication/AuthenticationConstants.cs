namespace RelistenUserService.Authentication;

public static class AuthenticationConstants
{
    public const string AuthSsoScheme = "Relisten.AuthSso";
    public const string WebSessionScheme = "Relisten.WebSession";
    public const string AuthSsoCookie = "__Host-relisten_auth";
    public const string WebSessionCookie = "__Host-relisten_session";
    public const string CsrfCookie = "__Host-relisten_csrf";
    public const string CsrfHeader = "X-Relisten-CSRF";
    public const string WebOriginHeader = "X-Relisten-Web-Origin";
    public const string WebClientId = "relisten-web";
    public const string CanonicalWebOrigin = "https://relisten.net";
    public const string LocalWebOrigin = "https://web.relisten.localhost:5173";
    public const string CanonicalWebCallback =
        "https://relisten.net/auth/session/callback";
    public const string LocalWebCallback =
        "https://web.relisten.localhost:5173/auth/session/callback";
    public const string CanonicalWebRegistration = "relisten-web-canonical";
    public const string LocalWebRegistration = "relisten-web-local";
    public const string GoogleProvider = "google";
    public const string AppleProvider = "apple";
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string AppleIssuer = "https://appleid.apple.com";
    public const string GoogleCallbackPath = "/signin-google";
    public const string AppleCallbackPath = "/signin-apple";
    public const string UserReadPolicy = "user.read";
    public const string LibraryReadPolicy = "library.read";
    public const string LibraryWritePolicy = "library.write";
    public const string AccountManagePolicy = "account.manage";
    public const string BrowserProfileReadPolicy = "browser.profile.read";
    public const string BrowserLibraryReadPolicy = "browser.library.read";
    public const string BrowserFavoriteMutationPolicy = "browser.favorite.mutate";
    public static readonly TimeSpan NativeSessionAbsoluteLifetime = TimeSpan.FromDays(180);
    public static readonly TimeSpan NativeSessionInactivityLimit = TimeSpan.FromDays(90);
}

public static class RelistenClaims
{
    public const string SessionId = "sid";
    public const string SecurityVersion = "security_version";
}

public static class RelistenScopes
{
    public const string UserRead = "user.read";
    public const string LibraryRead = "library.read";
    public const string LibraryWrite = "library.write";
    public const string AccountManage = "account.manage";

    public static readonly string[] Native =
    [
        UserRead,
        LibraryRead,
        LibraryWrite,
        AccountManage
    ];
}
