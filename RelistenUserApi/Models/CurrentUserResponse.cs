namespace Relisten.UserApi.Models;

public sealed class CurrentUserResponse
{
    public Guid UserUuid { get; init; }
    public required string DisplayName { get; init; }
    public required string Username { get; init; }
    public required string ScopeId { get; init; }
}
