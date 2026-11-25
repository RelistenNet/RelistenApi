namespace RelistenUserService.Authentication.Browser;

/// <summary>
/// Defines segment-safe routes that browser hosts and origin reconstruction may reach.
/// This list does not authorize a request. Endpoint policies and capabilities remain authoritative.
/// </summary>
public static class BrowserRouteBoundary
{
    public static bool CanRelay(PathString path) =>
        IsSessionLifecyclePath(path) || IsCsrfPath(path) || IsSharedResourcePath(path);

    public static bool IsSharedResourcePath(PathString path) =>
        path == "/v1/me"
        || path == "/v1/search"
        || IsLibraryPath(path)
        || IsPlaylistPath(path)
        || IsPublicPlaylistPath(path);

    internal static bool IsPublicPlaylistPath(PathString path) =>
        path.StartsWithSegments("/v1/public-playlists");

    public static bool IsPlaylistPath(PathString path) => path.StartsWithSegments("/v1/playlists");

    public static bool IsLibraryPath(PathString path) => path.StartsWithSegments("/v1/library");

    public static bool IsSessionLifecyclePath(PathString path) =>
        path.StartsWithSegments("/auth/session");

    public static bool IsCsrfPath(PathString path) => path == "/api/user/v1/csrf";
}
