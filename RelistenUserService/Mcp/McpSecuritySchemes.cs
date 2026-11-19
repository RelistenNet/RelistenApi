using RelistenUserService.Authentication;

namespace RelistenUserService.Mcp;

/// <summary>
/// The <c>securitySchemes</c> tool metadata that tells a client whether to call a tool
/// anonymously or to link an account first. A tool that lists <c>noauth</c> beside an OAuth
/// scheme works anonymously and unlocks an optional signed-in feature with that scope.
/// </summary>
internal static class McpSecuritySchemes
{
    public const string NoAuth = """[{"type":"noauth"}]""";

    public const string UserRead =
        $$"""[{"type":"oauth2","scopes":["{{RelistenScopes.UserRead}}"]}]""";
    public const string LibraryRead =
        $$"""[{"type":"oauth2","scopes":["{{RelistenScopes.LibraryRead}}"]}]""";
    public const string LibraryWrite =
        $$"""[{"type":"oauth2","scopes":["{{RelistenScopes.LibraryWrite}}"]}]""";

    public const string NoAuthOrUserRead =
        $$"""[{"type":"noauth"},{"type":"oauth2","scopes":["{{RelistenScopes.UserRead}}"]}]""";
    public const string NoAuthOrLibraryRead =
        $$"""[{"type":"noauth"},{"type":"oauth2","scopes":["{{RelistenScopes.LibraryRead}}"]}]""";
}
