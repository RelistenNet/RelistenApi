namespace Relisten.Services.Auth
{
    public class ApplicationRole
    {
        public string RoleId { get; set; }
        public string RoleName { get; set; }
        public string RoleNameNormalized => RoleName?.ToUpper();
    }
}
