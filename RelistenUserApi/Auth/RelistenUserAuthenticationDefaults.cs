namespace Relisten.UserApi.Auth;

public static class RelistenUserAuthenticationDefaults
{
    public const string Scheme = "RelistenUser";

    public static class ClaimTypes
    {
        public const string UserUuid = "relisten:user_uuid";
        public const string DisplayName = "relisten:display_name";
        public const string Username = "relisten:username";
        public const string ScopeId = "relisten:scope_id";
    }
}
