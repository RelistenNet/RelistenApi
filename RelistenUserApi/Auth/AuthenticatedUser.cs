namespace Relisten.UserApi.Auth;

public sealed record AuthenticatedUser(
    Guid UserUuid,
    string DisplayName,
    string Username,
    string ScopeId,
    Guid? SessionUuid = null);
