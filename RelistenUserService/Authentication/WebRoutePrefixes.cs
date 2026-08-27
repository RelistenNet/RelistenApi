namespace RelistenUserService.Authentication;

public static class WebRoutePrefixes
{
    public static bool Contains(PathString path) =>
        path.StartsWithSegments("/auth/session")
        || path.StartsWithSegments("/api/user/v1");
}
