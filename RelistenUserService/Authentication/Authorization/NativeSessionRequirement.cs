using Microsoft.AspNetCore.Authorization;

namespace RelistenUserService.Authentication.Authorization;

public sealed class NativeSessionRequirement : IAuthorizationRequirement;

public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;
