namespace Relisten.UserApi.Auth;

public interface IAuthenticatedUserContext
{
    AuthenticatedUser CurrentUser { get; }
}
