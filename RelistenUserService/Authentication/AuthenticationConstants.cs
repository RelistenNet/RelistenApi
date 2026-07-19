namespace RelistenUserService.Authentication;

public static class AuthenticationConstants
{
    public const string DevelopmentIdentityScheme = "Relisten.DevelopmentIdentity";
    public const string UserReadPolicy = "user.read";
    public const string LibraryReadPolicy = "library.read";
    public const string LibraryWritePolicy = "library.write";
    public const string AccountManagePolicy = "account.manage";
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
